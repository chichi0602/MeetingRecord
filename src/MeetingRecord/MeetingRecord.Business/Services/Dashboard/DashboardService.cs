using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Dashboard;

/// <summary>
/// 儀表板的統計聚合。
///
/// <para>
/// **資料範圍沿用各頁既有的規則**：會議與待辦套團隊過濾（<c>MeetingService</c>／
/// <c>TodoService</c> 本來就這樣做），專案不套（0.4.39 已移除專案的列級權控）。
/// 否則會出現「儀表板說有 10 場會議、點進去只看得到 6 場」這種對不起來的狀況。
/// </para>
///
/// <para>
/// 刻意不做快取：這是十來個聚合查詢，在 SQLite 上很快，而儀表板本來就該顯示當下的數字。
/// </para>
/// </summary>
public class DashboardService
{
    /// <summary>趨勢圖預設顯示的天數；畫面可切換 7／14／30／90。</summary>
    private const int DefaultTrendDays = 30;

    /// <summary>長條圖最多列出幾個專案，超過的併不進來——排名圖列太多就失去重點。</summary>
    private const int TopProjectCount = 8;

    private const string UnassignedProjectLabel = "未歸屬";

    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly AiChatStore chatStore;
    private readonly ILogger<DashboardService> logger;

    public DashboardService(
        BackendDBContext context,
        IRecordAccessScopeProvider accessScope,
        AiChatStore chatStore,
        ILogger<DashboardService> logger)
    {
        this.context = context;
        this.accessScope = accessScope;
        this.chatStore = chatStore;
        this.logger = logger;
    }

    public async Task<DashboardSummary> GetSummaryAsync(int trendDays = DefaultTrendDays, CancellationToken cancellationToken = default)
    {
        var scope = await accessScope.GetAsync();
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);

        var meetings = BuildMeetingQuery(scope);
        var todos = BuildTodoQuery(scope);

        // 一次把需要的欄位取回來再於記憶體分組：資料量在這個系統的量級很小，
        // 而分成十幾條 GroupBy 查詢反而更慢，也更難讀。
        var meetingFacts = await meetings
            .Select(x => new MeetingFact(
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
            .Select(x => new { x.Status })
            .ToListAsync(cancellationToken);

        var todoFacts = await todos
            .Select(x => new { x.Status, x.DueDate })
            .ToListAsync(cancellationToken);

        // 0.4.60 起對話存在檔案系統而不是資料庫，所以這裡是掃對話檔而不是 COUNT(*)。
        var chatQuestionCount = chatStore.CountQuestions();

        logger.LogDebug(
            "Dashboard summary loaded. Meetings={Meetings}, Projects={Projects}, Todos={Todos}",
            meetingFacts.Count,
            projects.Count,
            todoFacts.Count);

        return new DashboardSummary(
            BuildCards(projects.Select(x => x.Status).ToList(), meetingFacts, todoFacts.Count, chatQuestionCount, monthStart, today,
                todoFacts.Count(x => !IsTodoDone(x.Status)),
                todoFacts.Count(x => !IsTodoDone(x.Status) && x.DueDate is not null && x.DueDate.Value.Date < today)),
            BuildProjectStatus(projects.Select(x => x.Status).ToList()),
            BuildTranscriptionStatus(meetingFacts),
            BuildDraftStatus(meetingFacts),
            BuildMeetingsPerProject(meetingFacts),
            BuildPromptTemplateUsage(meetingFacts),
            DashboardMetrics.BuildDailyTrend(
                meetingFacts.Select(x => (x.CreatedAt, x.DraftCompletedAt)),
                DateOnly.FromDateTime(today),
                trendDays),
            BuildPerformance(meetingFacts));
    }

    #region 查詢範圍

    private IQueryable<Meeting> BuildMeetingQuery(RecordAccessScope scope)
    {
        IQueryable<Meeting> query = context.Meeting.AsNoTracking().Include(x => x.Project);

        return scope.IsAdmin
            ? query
            : query.Where(TagStringHelper.BuildTeamAccessPredicate<Meeting>(x => x.Teams, scope.Teams));
    }

    private IQueryable<Todo> BuildTodoQuery(RecordAccessScope scope)
    {
        IQueryable<Todo> query = context.Todo.AsNoTracking();

        return scope.IsAdmin
            ? query
            : query.Where(TagStringHelper.BuildTeamAccessPredicate<Todo>(x => x.Teams, scope.Teams));
    }

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
        int overdueTodoCount)
    {
        var inProgressProjects = projectStatuses.Count(status => status == "進行中");
        var newThisMonth = meetings.Count(x => x.CreatedAt >= monthStart);
        var transcribed = meetings.Count(x => x.TranscriptionStatus == TranscriptionStatus.Completed);
        var transcribing = meetings.Count(x =>
            x.TranscriptionStatus is TranscriptionStatus.Pending or TranscriptionStatus.Processing);
        var mediaBytes = meetings.Sum(x => x.MediaFileSize ?? 0);

        return
        [
            new StatCardItem("專案總數", projectStatuses.Count.ToString(), $"進行中 {inProgressProjects}"),
            new StatCardItem("會議紀錄", meetings.Count.ToString(), $"本月新增 {newThisMonth}"),
            new StatCardItem("逐字稿完成", transcribed.ToString(), $"處理中 {transcribing}",
                transcribing > 0 ? ChartTone.Warning : ChartTone.Neutral),
            new StatCardItem("待辦未完成", openTodoCount.ToString(),
                overdueTodoCount > 0 ? $"已逾期 {overdueTodoCount}" : $"共 {totalTodoCount} 筆",
                overdueTodoCount > 0 ? ChartTone.Danger : ChartTone.Neutral),
            new StatCardItem("音檔總容量", FileSizeFormatter.Describe(mediaBytes), $"截至 {today:yyyy/MM/dd}"),
            new StatCardItem("AI 問答次數", chatQuestionCount.ToString(), "累計提問"),
        ];
    }

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
            meetings.Count(x => x.TranscriptionStatus == TranscriptionStatus.Completed && x.ProjectId is null));
    }

    #endregion

    /// <summary>從資料庫取回的會議欄位投影，避免把整個 Meeting 實體撈進記憶體。</summary>
    private sealed record MeetingFact(
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
}
