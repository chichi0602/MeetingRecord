using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Dashboard;

/// <summary>
/// 儀表板的統計聚合。
///
/// <para>
/// **0.4.97 起一律是全公司的數字，刻意不套團隊過濾**——與會議、提示詞範本頁不同。
/// 儀表板登入即可看、所有人看到同一份，所以只能放彙總數字與圖表，**不可加入任何明細清單**，
/// 否則會把別的團隊看不到的會議內容露出來。
/// </para>
///
/// <para>
/// 刻意不做快取：這是十來個聚合查詢，在 SQLite 上很快，而儀表板本來就該顯示當下的數字。
/// </para>
/// </summary>
public class DashboardService
{
    /// <summary>長條圖最多列出幾個專案，超過的併不進來——排名圖列太多就失去重點。</summary>
    private const int TopProjectCount = 8;

    private const string UnassignedProjectLabel = "未歸屬";

    private readonly BackendDBContext context;
    private readonly AiChatStore chatStore;
    private readonly string transcriptRootPath;
    private readonly ILogger<DashboardService> logger;

    public DashboardService(
        BackendDBContext context,
        AiChatStore chatStore,
        IOptions<SystemSettings> systemSettings,
        ILogger<DashboardService> logger)
    {
        this.context = context;
        this.chatStore = chatStore;
        // 根目錄一律取自 SystemSettings.ExternalFileSystem，比照 MeetingFileStore／AiChatStore。
        transcriptRootPath = systemSettings.Value.ExternalFileSystem.MeetingTranscriptPath;
        this.logger = logger;
    }

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);

        // 一次把需要的欄位取回來再於記憶體分組：資料量在這個系統的量級很小，
        // 而分成十幾條 GroupBy 查詢反而更慢，也更難讀。
        var meetingFacts = await context.Meeting.AsNoTracking()
            .Select(x => new MeetingFact(
                x.Id,
                x.TranscriptionStatus,
                x.DraftStatus,
                x.CreatedAt,
                x.DraftCompletedAt,
                x.MediaFileSize,
                x.ProjectId,
                x.Project != null ? x.Project.Title : null,
                x.DraftPromptTemplateName,
                x.TranscriptionStartedAt,
                x.TranscriptionCompletedAt,
                x.DraftStartedAt))
            .ToListAsync(cancellationToken);

        var projects = await context.Project.AsNoTracking()
            .Select(x => new ProjectFact(x.Status, x.EndDate, x.CompletionPercentage))
            .ToListAsync(cancellationToken);

        var todoFacts = await context.Todo.AsNoTracking()
            .Select(x => new TodoFact(x.Status, x.DueDate, x.Priority, x.MeetingId))
            .ToListAsync(cancellationToken);

        var promptTemplateFacts = await context.PromptTemplate.AsNoTracking()
            .Select(x => new PromptTemplateFact(x.Name, x.IsEnabled))
            .ToListAsync(cancellationToken);

        // AudioSeconds 是 REAL 不是 decimal，在 SQL 端篩選沒有 TEXT 比較的問題。
        var transcriptionUsages = await context.AiUsageLog.AsNoTracking()
            .Where(x => x.Feature == AiUsageFeature.Transcription
                && x.Outcome == AiUsageOutcome.Succeeded
                && x.MeetingId != null
                && x.AudioSeconds != null)
            .Select(x => new { MeetingId = x.MeetingId!.Value, x.OccurredAt, Seconds = x.AudioSeconds!.Value })
            .ToListAsync(cancellationToken);

        var attachmentBytes = await context.ProjectFile.AsNoTracking()
            .SumAsync(x => (long?)x.FileSize, cancellationToken) ?? 0L;

        // 0.4.60 起對話存在檔案系統而不是資料庫，所以這裡是掃對話檔而不是 COUNT(*)。
        var chatQuestionCount = chatStore.CountQuestions();

        // 逐字稿沒有容量欄位（Meeting 只記相對路徑），只能實際量目錄。
        var storage = new StorageSummary(
            meetingFacts.Sum(x => x.MediaFileSize ?? 0),
            DirectorySizeCalculator.Measure(transcriptRootPath),
            attachmentBytes);

        logger.LogDebug(
            "Dashboard summary loaded. Meetings={Meetings}, Projects={Projects}, Todos={Todos}, PromptTemplates={PromptTemplates}, StorageBytes={StorageBytes}",
            meetingFacts.Count,
            projects.Count,
            todoFacts.Count,
            promptTemplateFacts.Count,
            storage.TotalBytes);

        return new DashboardSummary(
            BuildCards(projects.Select(x => x.Status).ToList(), meetingFacts, todoFacts.Count, chatQuestionCount, monthStart, today,
                todoFacts.Count(x => !IsTodoDone(x.Status)),
                todoFacts.Count(x => !IsTodoDone(x.Status) && x.DueDate is not null && x.DueDate.Value.Date < today),
                storage),
            BuildProjectStatus(projects.Select(x => x.Status).ToList()),
            BuildTranscriptionStatus(meetingFacts),
            BuildDraftStatus(meetingFacts),
            BuildMeetingsPerProject(meetingFacts),
            BuildPromptTemplateUsage(meetingFacts),
            BuildOpenTodoPriority(todoFacts),
            BuildPerformance(meetingFacts),
            BuildPromptTemplates(promptTemplateFacts, meetingFacts),
            storage,
            BuildTodoOverview(todoFacts, today),
            BuildProjectOverview(projects, today),
            BuildMeetingHours(
                meetingFacts,
                transcriptionUsages.Select(x => (x.MeetingId, x.OccurredAt, x.Seconds)),
                monthStart));
    }

    #region 概況指標

    /// <summary>「7 天內到期」的天數（含今天）。</summary>
    private const int TodoDueSoonDays = 7;

    /// <summary>專案「即將到期」的天數（含今天）。專案週期比待辦長，所以看得比較遠。</summary>
    private const int ProjectDueSoonDays = 14;

    private static TodoOverview BuildTodoOverview(IReadOnlyList<TodoFact> todos, DateTime today)
    {
        var open = todos.Where(x => !IsTodoDone(x.Status)).ToList();
        var dueSoonEnd = today.AddDays(TodoDueSoonDays);

        return new TodoOverview(
            open.Count(x => x.DueDate is not null && x.DueDate.Value.Date < today),
            open.Count(x => x.DueDate is not null && x.DueDate.Value.Date >= today && x.DueDate.Value.Date <= dueSoonEnd),
            todos.Count(x => x.Status == TodoInProgressStatus),
            // 沒有任何待辦時回 null 讓畫面顯示「—」：0% 會被讀成「都是手動建立的」。
            todos.Count == 0 ? null : todos.Count(x => x.MeetingId is not null) * 100d / todos.Count);
    }

    private static ProjectOverview BuildProjectOverview(IReadOnlyList<ProjectFact> projects, DateTime today)
    {
        var inProgress = projects.Where(x => x.Status == ProjectInProgressStatus).ToList();
        var unfinished = projects.Where(x => x.Status != ProjectCompletedStatus).ToList();
        var dueSoonEnd = today.AddDays(ProjectDueSoonDays);

        return new ProjectOverview(
            inProgress.Count == 0 ? null : inProgress.Average(x => (double)x.CompletionPercentage),
            unfinished.Count(x => x.EndDate is not null && x.EndDate.Value.Date < today),
            unfinished.Count(x => x.EndDate is not null && x.EndDate.Value.Date >= today && x.EndDate.Value.Date <= dueSoonEnd),
            projects.Count(x => x.Status is "暫緩" or "等待"));
    }

    /// <summary>
    /// 會議時數。只看**轉錄完成**的會議：還在跑或失敗的那一輪是不完整的分段，
    /// 算進去會讓會議看起來比實際短。
    /// </summary>
    private static MeetingHoursSummary BuildMeetingHours(
        IReadOnlyList<MeetingFact> meetings,
        IEnumerable<(int MeetingId, DateTime OccurredAt, double Seconds)> usages,
        DateTime monthStart)
    {
        var transcribed = meetings
            .Where(x => x.TranscriptionStatus == TranscriptionStatus.Completed)
            .ToList();

        var seconds = DashboardMetrics.SumLatestRunAudioSeconds(
            usages,
            transcribed
                .Where(x => x.TranscriptionStartedAt is not null)
                .ToDictionary(x => x.Id, x => x.TranscriptionStartedAt!.Value));

        var thisMonthIds = transcribed
            .Where(x => x.CreatedAt >= monthStart)
            .Select(x => x.Id)
            .ToHashSet();

        var total = seconds.Values.Sum();

        return new MeetingHoursSummary(
            DescribeSeconds(seconds.Count == 0 ? null : total),
            DescribeSeconds(seconds.Count == 0 ? null : seconds.Where(x => thisMonthIds.Contains(x.Key)).Sum(x => x.Value)),
            DescribeSeconds(seconds.Count == 0 ? null : total / seconds.Count),
            seconds.Count,
            transcribed.Count);
    }

    private static string DescribeSeconds(double? seconds)
        => DashboardMetrics.DescribeDuration(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value));

    #endregion

    #region 數字卡

    private static IReadOnlyList<StatCardItem> BuildCards(
        IReadOnlyList<string> projectStatuses,
        IReadOnlyList<MeetingFact> meetings,
        int totalTodoCount,
        int chatQuestionCount,
        DateTime monthStart,
        DateTime today,
        int openTodoCount,
        int overdueTodoCount,
        StorageSummary storage)
    {
        var inProgressProjects = projectStatuses.Count(status => status == "進行中");
        var newThisMonth = meetings.Count(x => x.CreatedAt >= monthStart);
        var transcribed = meetings.Count(x => x.TranscriptionStatus == TranscriptionStatus.Completed);
        var transcribing = meetings.Count(x =>
            x.TranscriptionStatus is TranscriptionStatus.Pending or TranscriptionStatus.Processing);

        return
        [
            new StatCardItem("專案總數", projectStatuses.Count.ToString(), $"進行中 {inProgressProjects}"),
            new StatCardItem("會議紀錄", meetings.Count.ToString(), $"本月新增 {newThisMonth}"),
            new StatCardItem("逐字稿完成", transcribed.ToString(), $"處理中 {transcribing}",
                transcribing > 0 ? ChartTone.Warning : ChartTone.Neutral),
            new StatCardItem("待辦未完成", openTodoCount.ToString(),
                overdueTodoCount > 0 ? $"已逾期 {overdueTodoCount}" : $"共 {totalTodoCount} 筆",
                overdueTodoCount > 0 ? ChartTone.Danger : ChartTone.Neutral),
            // 顯示三項合計而不是只有影音檔：下方「儲存空間」細分列的合計必須與這張卡同源，
            // 否則卡片 234 MB、細分合計 260 MB，看起來像兩個數字在打架。
            new StatCardItem("儲存空間", FileSizeFormatter.Describe(storage.TotalBytes), $"截至 {today:yyyy/MM/dd}"),
            new StatCardItem("AI 問答次數", chatQuestionCount.ToString(), "累計提問"),
        ];
    }

    private const string TodoInProgressStatus = "進行中";
    private const string ProjectInProgressStatus = "進行中";
    private const string ProjectCompletedStatus = "已完成";

    /// <summary>待辦的完成判斷只看 Status 一個欄位（見待辦事項 PRD 的設計決策）。</summary>
    private static bool IsTodoDone(string? status) => status == "已完成";

    #endregion

    #region 圓餅圖

    private static IReadOnlyList<ChartSlice> BuildProjectStatus(IReadOnlyList<string> statuses)
        => [.. ProjectAdapterModel.StatusOptions
            .Select(option => new ChartSlice(
                option,
                statuses.Count(status => status == option),
                option switch
                {
                    "已完成" => ChartTone.Success,
                    "進行中" => ChartTone.Warning,
                    _ => ChartTone.Neutral,
                }))
            .Where(slice => slice.Value > 0)];

    private static IReadOnlyList<ChartSlice> BuildTranscriptionStatus(IReadOnlyList<MeetingFact> meetings)
        => [.. Enum.GetValues<TranscriptionStatus>()
            .Select(status => new ChartSlice(
                TranscriptionStatusText.Describe(status),
                meetings.Count(x => x.TranscriptionStatus == status),
                ToneForStatus(status == TranscriptionStatus.Completed, status == TranscriptionStatus.Failed,
                    status is TranscriptionStatus.Pending or TranscriptionStatus.Processing)))
            .Where(slice => slice.Value > 0)];

    private static IReadOnlyList<ChartSlice> BuildDraftStatus(IReadOnlyList<MeetingFact> meetings)
        => [.. Enum.GetValues<DraftStatus>()
            .Select(status => new ChartSlice(
                DraftStatusText.Describe(status),
                meetings.Count(x => x.DraftStatus == status),
                ToneForStatus(status == DraftStatus.Completed, status == DraftStatus.Failed,
                    status is DraftStatus.Pending or DraftStatus.Processing)))
            .Where(slice => slice.Value > 0)];

    /// <summary>未完成待辦的優先度分布。由高到低排，讓紅色切片從 12 點鐘方向開始。</summary>
    private static IReadOnlyList<ChartSlice> BuildOpenTodoPriority(IReadOnlyList<TodoFact> todos)
    {
        var open = todos.Where(x => !IsTodoDone(x.Status)).ToList();

        return [.. TodoAdapterModel.PriorityOptions
            .Reverse()
            .Select(option => new ChartSlice(
                option,
                open.Count(x => x.Priority == option),
                option switch
                {
                    "高" => ChartTone.Danger,
                    "中" => ChartTone.Warning,
                    _ => ChartTone.Neutral,
                }))
            .Where(slice => slice.Value > 0)];
    }

    private static ChartTone ToneForStatus(bool isCompleted, bool isFailed, bool isRunning)
        => isFailed ? ChartTone.Danger
            : isCompleted ? ChartTone.Success
            : isRunning ? ChartTone.Warning
            : ChartTone.Neutral;

    #endregion

    #region 長條圖

    private static IReadOnlyList<ChartSlice> BuildMeetingsPerProject(IReadOnlyList<MeetingFact> meetings)
        => [.. meetings
            .GroupBy(x => x.ProjectTitle ?? UnassignedProjectLabel)
            .Select(group => new ChartSlice(
                group.Key,
                group.Count(),
                // 未歸屬是待處理的工作，用警示色點出來而不是混在一般色階裡。
                group.Key == UnassignedProjectLabel ? ChartTone.Warning : ChartTone.Neutral))
            .OrderByDescending(slice => slice.Value)
            .ThenBy(slice => slice.Label, StringComparer.Ordinal)
            .Take(TopProjectCount)];

    private static IReadOnlyList<ChartSlice> BuildPromptTemplateUsage(IReadOnlyList<MeetingFact> meetings)
        => [.. meetings
            .Where(x => !string.IsNullOrWhiteSpace(x.DraftPromptTemplateName))
            .GroupBy(x => x.DraftPromptTemplateName!)
            .Select(group => new ChartSlice(group.Key, group.Count()))
            .OrderByDescending(slice => slice.Value)
            .ThenBy(slice => slice.Label, StringComparer.Ordinal)];

    #endregion

    #region 效能指標

    private static PerformanceSummary BuildPerformance(IReadOnlyList<MeetingFact> meetings)
    {
        // SQLite／EF Core 無法直接對 TimeSpan 做 AVG，所以在記憶體算。
        // 資料量成長後要改成在 SQL 端以 julianday 相減。
        var transcriptionRanges = meetings
            .Where(x => x.TranscriptionStartedAt is not null && x.TranscriptionCompletedAt is not null)
            .Select(x => (x.TranscriptionStartedAt!.Value, x.TranscriptionCompletedAt!.Value));

        var draftRanges = meetings
            .Where(x => x.DraftStartedAt is not null && x.DraftCompletedAt is not null)
            .Select(x => (x.DraftStartedAt!.Value, x.DraftCompletedAt!.Value));

        return new PerformanceSummary(
            DashboardMetrics.DescribeDuration(DashboardMetrics.AverageDuration(transcriptionRanges)),
            DashboardMetrics.DescribeDuration(DashboardMetrics.AverageDuration(draftRanges)),
            DashboardMetrics.CalculateFailureRate(
                meetings.Count(x => x.TranscriptionStatus == TranscriptionStatus.Completed),
                meetings.Count(x => x.TranscriptionStatus == TranscriptionStatus.Failed)),
            // 0.4.73 起「未歸屬但已產生會議紀錄」是正常終態（不必先建專案就能生成），
            // 只數 ProjectId is null 的話這個黃色告警會一直亮著一個不存在的問題。
            meetings.Count(x =>
                x.TranscriptionStatus == TranscriptionStatus.Completed
                && x.ProjectId is null
                && x.DraftStatus != DraftStatus.Completed));
    }

    #endregion

    #region 提示詞範本

    private static PromptTemplateSummary BuildPromptTemplates(
        IReadOnlyList<PromptTemplateFact> templates,
        IReadOnlyList<MeetingFact> meetings)
    {
        var enabled = templates.Count(x => x.IsEnabled);

        return new PromptTemplateSummary(
            templates.Count,
            enabled,
            templates.Count - enabled,
            DashboardMetrics.CountUnusedEnabledTemplates(
                templates.Select(x => (x.Name, x.IsEnabled)),
                meetings.Select(x => x.DraftPromptTemplateName)));
    }

    #endregion

    /// <summary>從資料庫取回的會議欄位投影，避免把整個 Meeting 實體撈進記憶體。</summary>
    private sealed record MeetingFact(
        int Id,
        TranscriptionStatus TranscriptionStatus,
        DraftStatus DraftStatus,
        DateTime CreatedAt,
        DateTime? DraftCompletedAt,
        long? MediaFileSize,
        int? ProjectId,
        string? ProjectTitle,
        string? DraftPromptTemplateName,
        DateTime? TranscriptionStartedAt,
        DateTime? TranscriptionCompletedAt,
        DateTime? DraftStartedAt);

    /// <summary>提示詞範本的投影。只需要名稱（比對使用情形）與啟用狀態。</summary>
    private sealed record PromptTemplateFact(string Name, bool IsEnabled);

    private sealed record ProjectFact(string Status, DateTime? EndDate, int CompletionPercentage);

    private sealed record TodoFact(string Status, DateTime? DueDate, string Priority, int? MeetingId);
}
