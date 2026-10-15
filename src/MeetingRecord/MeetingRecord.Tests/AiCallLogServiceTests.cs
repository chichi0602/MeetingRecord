using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Web.Components.Views.AiManagement;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 對話紀錄的寫入、連帶刪除與保留期限（1.0.29，移植自 Starter 的 AiCallLogServiceTests 並改成本專案的功能別）。
/// </summary>
public sealed class AiCallLogServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));

    public AiCallLogServiceTests()
    {
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        Directory.CreateDirectory(rootPath);
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private AiCallLogService CreateService(AiCallLogSettings? settings = null)
        => TestAiServices.CreateCallLogService(context, rootPath, settings);

    private static AiCallLogEntry Entry(
        DateTime? occurredAt = null,
        int? meetingId = null,
        int? projectId = null,
        string? relatedInfo = "會議 #1 會議紀錄") => new()
        {
            CallId = Guid.NewGuid(),
            OccurredAt = occurredAt ?? new DateTime(2026, 10, 15, 9, 0, 0),
            Feature = AiUsageFeature.MeetingDraft,
            FeatureText = "會議紀錄生成",
            Outcome = AiUsageOutcome.Succeeded,
            Provider = "AzureOpenAI",
            Model = "gpt-5.6-sol",
            UserId = 7,
            UserName = "王小明",
            MeetingId = meetingId,
            ProjectId = projectId,
            RelatedInfo = relatedInfo,
            Request = new AiCallLogRequest
            {
                Messages = [new() { Role = "system", Content = "系統" }, new() { Role = "user", Content = "逐字稿內容" }],
            },
            Response = new AiCallLogResponse { Text = "會議紀錄草稿" },
        };

    [Fact]
    public async Task Record_ShouldWriteRowAndReadableContentFile()
    {
        var entry = Entry(meetingId: 1);

        await CreateService().RecordAsync(entry);

        var row = await context.AiCallLog.AsNoTracking().SingleAsync();
        Assert.Equal(entry.CallId, row.CallId);
        Assert.Equal(7, row.RequestCharacters);           // 「系統」2 ＋「逐字稿內容」5
        Assert.Equal(6, row.ResponseCharacters);
        Assert.Equal($"202610/{entry.CallId:N}.json", row.ContentFile);

        var json = await File.ReadAllTextAsync(Path.Combine(rootPath, "202610", $"{entry.CallId:N}.json"));
        Assert.Contains("逐字稿內容", json);              // 中文不跳脫：檔案是給人看的
        var parsed = JsonSerializer.Deserialize<AiCallLogEntry>(json, AiCallLogService.FileJsonOptions)!;
        Assert.Equal("會議紀錄草稿", parsed.Response.Text);
        Assert.Equal(AiUsageFeature.MeetingDraft, parsed.Feature);
    }

    [Fact]
    public async Task Record_Disabled_ShouldWriteNothing()
    {
        await CreateService(new AiCallLogSettings { Enabled = false }).RecordAsync(Entry());

        Assert.False(await context.AiCallLog.AnyAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(rootPath));
    }

    [Fact]
    public async Task Record_ShouldTruncateRelatedInfo()
    {
        await CreateService().RecordAsync(Entry(relatedInfo: new string('長', 800)));

        Assert.Equal(500, (await context.AiCallLog.AsNoTracking().SingleAsync()).RelatedInfo!.Length);
    }

    [Fact]
    public async Task Record_FileStoreUnavailable_ShouldStillInsertTheRow()
    {
        var service = TestAiServices.CreateCallLogService(context, aiCallLogPath: string.Empty);

        await service.RecordAsync(Entry());

        Assert.Null((await context.AiCallLog.AsNoTracking().SingleAsync()).ContentFile);
    }

    [Fact]
    public async Task Record_DatabaseFailure_ShouldNotThrowAndShouldRemoveTheFile()
    {
        var service = CreateService();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE \"AiCallLog\"");

        await service.RecordAsync(Entry());

        // 列沒建起來就不留檔案（孤兒）；也不拋例外。
        Assert.Empty(Directory.EnumerateFiles(rootPath, "*.json", SearchOption.AllDirectories));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task DeleteByMeeting_ShouldRemoveRowsAndFilesOfThatMeetingOnly()
    {
        var service = CreateService();
        var mine = Entry(meetingId: 1);
        var other = Entry(meetingId: 2);
        await service.RecordAsync(mine);
        await service.RecordAsync(other);

        var removed = await service.DeleteByMeetingAsync(1);

        Assert.Equal(1, removed);
        Assert.Equal(other.CallId, (await context.AiCallLog.AsNoTracking().SingleAsync()).CallId);
        Assert.False(File.Exists(Path.Combine(rootPath, "202610", $"{mine.CallId:N}.json")));
        Assert.True(File.Exists(Path.Combine(rootPath, "202610", $"{other.CallId:N}.json")));
    }

    [Fact]
    public async Task DeleteByProject_ShouldOnlyRemoveProjectScopeRows()
    {
        // 刪專案不會刪它底下的會議（外鍵 SetNull），會議的紀錄跟著會議走。
        var service = CreateService();
        var projectChat = Entry(projectId: 5);
        var meetingInProject = Entry(meetingId: 9, projectId: 5);
        await service.RecordAsync(projectChat);
        await service.RecordAsync(meetingInProject);

        var removed = await service.DeleteByProjectAsync(5);

        Assert.Equal(1, removed);
        Assert.Equal(meetingInProject.CallId, (await context.AiCallLog.AsNoTracking().SingleAsync()).CallId);
    }

    [Fact]
    public async Task PurgeExpired_ShouldRespectRetentionAndSweepOldMonthFolders()
    {
        var now = new DateTime(2026, 10, 15, 9, 0, 0);
        var service = CreateService(new AiCallLogSettings { RetentionDays = 90 });
        var old = Entry(occurredAt: now.AddDays(-91));
        var recent = Entry(occurredAt: now.AddDays(-89));
        await service.RecordAsync(old);
        await service.RecordAsync(recent);

        // 孤兒檔：檔已寫、列沒建起來，放在更早的月份。
        var orphanFolder = Path.Combine(rootPath, "202601");
        Directory.CreateDirectory(orphanFolder);
        await File.WriteAllTextAsync(Path.Combine(orphanFolder, "orphan.json"), "{}");

        var removed = await service.PurgeExpiredAsync(now);

        Assert.Equal(1, removed);
        Assert.Equal(recent.CallId, (await context.AiCallLog.AsNoTracking().SingleAsync()).CallId);
        Assert.False(Directory.Exists(orphanFolder));
    }

    // ───────── 1.0.30：AI 對話紀錄頁的查詢、明細與刪除 ─────────

    [Fact]
    public async Task Get_ShouldFilterSortAndPage()
    {
        var service = CreateService();
        var day = new DateTime(2026, 10, 15, 9, 0, 0);
        await service.RecordAsync(Entry(occurredAt: day.AddMinutes(1), relatedInfo: "會議 #1 分段摘要 1/2"));
        await service.RecordAsync(Entry(occurredAt: day.AddMinutes(2), relatedInfo: "會議 #1 分段摘要 2/2"));
        var chat = Entry(occurredAt: day.AddMinutes(3), relatedInfo: "專案 #5 AI 問答：進度");
        chat.Feature = AiUsageFeature.AiChat;
        chat.Outcome = AiUsageOutcome.Failed;
        await service.RecordAsync(chat);

        var byKeyword = await service.GetAsync(new AiCallLogQuery { Keyword = "分段摘要", PageSize = 1 });
        Assert.Equal(2, byKeyword.Count);
        Assert.Equal("會議 #1 分段摘要 2/2", Assert.Single(byKeyword.Result).RelatedInfo);   // 預設新到舊

        var byFeature = await service.GetAsync(new AiCallLogQuery { Feature = AiUsageFeature.AiChat, Outcome = AiUsageOutcome.Failed });
        Assert.Equal("失敗", Assert.Single(byFeature.Result).OutcomeText);

        var byCallId = await service.GetAsync(new AiCallLogQuery { Keyword = chat.CallId.ToString() });
        Assert.Equal(chat.CallId, Assert.Single(byCallId.Result).CallId);

        var oldestFirst = await service.GetAsync(new AiCallLogQuery { SortField = nameof(AiCallLogAdapterModel.OccurredAt), SortDescending = false });
        Assert.Equal("會議 #1 分段摘要 1/2", oldestFirst.Result.First().RelatedInfo);
    }

    [Fact]
    public async Task Detail_ShouldIncludeContentUsageAndThread()
    {
        var service = CreateService();
        var first = Entry(meetingId: 1);
        first.ThreadKey = "draft:1:run";
        var second = Entry(meetingId: 1, occurredAt: first.OccurredAt.AddMinutes(1));
        second.ThreadKey = "draft:1:run";
        await service.RecordAsync(first);
        await service.RecordAsync(second);
        context.AiUsageLog.Add(new AiUsageLog
        {
            Feature = AiUsageFeature.MeetingDraft,
            Provider = "AzureOpenAI",
            Model = "gpt-5.6-sol",
            InputTokens = 1000,
            OutputTokens = 200,
            EstimatedCost = 0.008m,
            Currency = "USD",
            CallId = second.CallId,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var id = (await service.FindIdByCallIdAsync(second.CallId))!.Value;
        var detail = (await service.GetDetailAsync(id))!;

        Assert.Equal("會議紀錄草稿", detail.Content!.Response.Text);
        Assert.Contains("逐字稿內容", detail.RawJson);
        Assert.Equal(1000, detail.Usage!.InputTokens);
        Assert.Equal([first.CallId, second.CallId], detail.Thread.Select(x => x.CallId));
    }

    [Fact]
    public async Task Detail_MissingContentFile_ShouldStillReturnTheRow()
    {
        var service = CreateService();
        var entry = Entry();
        await service.RecordAsync(entry);
        File.Delete(Path.Combine(rootPath, "202610", $"{entry.CallId:N}.json"));

        var detail = await service.GetDetailAsync((await service.FindIdByCallIdAsync(entry.CallId))!.Value);

        Assert.NotNull(detail);
        Assert.Null(detail!.Content);
        Assert.Null(detail.Usage);
        Assert.Single(detail.Thread);
    }

    [Fact]
    public async Task DeletePurgeAndClearAll_ShouldRemoveRowsAndFiles()
    {
        var service = CreateService();
        var day = new DateTime(2026, 10, 15, 9, 0, 0);
        var old = Entry(occurredAt: day.AddDays(-10));
        var recent = Entry(occurredAt: day);
        var another = Entry(occurredAt: day.AddMinutes(5));
        await service.RecordAsync(old);
        await service.RecordAsync(recent);
        await service.RecordAsync(another);

        Assert.Equal(1, await service.PurgeBeforeAsync(day.Date));
        Assert.True((await service.DeleteAsync((await service.FindIdByCallIdAsync(recent.CallId))!.Value)).Success);
        Assert.False(File.Exists(Path.Combine(rootPath, "202610", $"{recent.CallId:N}.json")));
        Assert.False((await service.DeleteAsync(99999)).Success);

        Assert.True((await service.ClearAllAsync()).Success);
        Assert.False(await context.AiCallLog.AnyAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(rootPath));
    }

    [Fact]
    public async Task FilterOptions_ShouldListUsersAndModelsSeen()
    {
        var service = CreateService();
        await service.RecordAsync(Entry());
        var other = Entry();
        other.Model = "gpt-4o-mini";
        other.UserId = null;
        other.UserName = null;
        await service.RecordAsync(other);

        var options = await service.GetFilterOptionsAsync();

        Assert.Equal([(7, "王小明")], options.Users);
        Assert.Equal(["gpt-4o-mini", "gpt-5.6-sol"], options.Models);
    }

    [Fact]
    public void View_CsvColumns_ShouldNotContainTheContent()
    {
        // 匯出只有中繼資料：內容是逐字稿與問答全文，要看請進明細窗（有稽核）。
        var headers = AiCallLogView.CsvColumns.Select(x => x.Header).ToList();

        Assert.Contains("呼叫識別碼", headers);
        Assert.DoesNotContain(headers, x => x.Contains("內容") || x.Contains("回應文字") || x.Contains("Prompt"));
    }

    [Fact]
    public void View_BuildBlocks_ShouldFollowTheChosenMode()
    {
        var entry = Entry();
        entry.Request.Images.Add(new AiCallLogImage { MediaType = "image/png", Bytes = 2048 });
        var detail = new AiCallLogDetail(new AiCallLogAdapterModel(), entry, "{}", null, []);

        var conversation = AiCallLogView.BuildBlocks(detail, AiCallLogView.ViewMode.Conversation);
        Assert.Equal(["系統指示", "送出內容", "附帶圖片", "回應"], conversation.Select(x => x.Label));
        Assert.True(conversation[^1].IsAssistant);

        Assert.DoesNotContain(AiCallLogView.BuildBlocks(detail, AiCallLogView.ViewMode.Request), x => x.IsAssistant);
        Assert.Equal("會議紀錄草稿", Assert.Single(AiCallLogView.BuildBlocks(detail, AiCallLogView.ViewMode.Response)).Content);
    }

    [Fact]
    public void View_BuildBlocks_TranscriptionShouldDescribeTheAudioInsteadOfMessages()
    {
        var entry = Entry();
        entry.Request = new AiCallLogRequest
        {
            Kind = AiCallLogRequest.TranscriptionKind,
            FileName = "part-001.mp3",
            AudioSeconds = 900,
            SegmentIndex = 2,
            SegmentCount = 3,
        };
        var detail = new AiCallLogDetail(new AiCallLogAdapterModel(), entry, "{}", null, []);

        var request = Assert.Single(AiCallLogView.BuildBlocks(detail, AiCallLogView.ViewMode.Request));

        Assert.Contains("part-001.mp3（第 2/3 段）", request.Content);
        Assert.Contains("00:15:00", request.Content);
        Assert.Contains("音訊本身不保存", request.Content);
    }

    [Fact]
    public async Task PurgeExpired_ShouldStillRunWhenRecordingIsDisabled()
    {
        var now = new DateTime(2026, 10, 15, 9, 0, 0);
        await CreateService().RecordAsync(Entry(occurredAt: now.AddDays(-200)));

        var removed = await CreateService(new AiCallLogSettings { Enabled = false, RetentionDays = 90 }).PurgeExpiredAsync(now);

        Assert.Equal(1, removed);
    }
}

/// <summary>AI 對話紀錄與用量帳本保留期限的設定驗證（1.0.29）。</summary>
public sealed class AiLogSettingsTests
{
    [Fact]
    public void AiCallLogSettings_Defaults_ShouldBeEnabledWith90Days()
    {
        var settings = new AiCallLogSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(90, settings.RetentionDays);
    }

    [Theory]
    [InlineData(0, false)]      // 對話紀錄沒有「永久保留」：內容敏感
    [InlineData(1, true)]
    [InlineData(3650, true)]
    [InlineData(3651, false)]
    public void AiCallLogSettings_RetentionDays_ShouldBeInRange(int days, bool valid)
        => Assert.Equal(valid, IsValid(new AiCallLogSettings { RetentionDays = days }));

    [Theory]
    [InlineData(0, true)]       // 預設：永久保留
    [InlineData(30, false)]     // 會清掉上個月，本月 vs 上月與每月上限都會算錯
    [InlineData(61, false)]
    [InlineData(62, true)]
    [InlineData(365, true)]
    public void LogRetentionSettings_AiUsageLogDays_ShouldBeZeroOrAtLeast62(int days, bool valid)
        => Assert.Equal(valid, IsValid(new LogRetentionSettings { AiUsageLogDays = days }));

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(5000, 300, true)]
    [InlineData(-1, 0, false)]
    public void AiQuotaSettings_ShouldRejectNegative(int global, int perUser, bool valid)
        => Assert.Equal(valid, IsValid(new AiQuotaSettings { GlobalMonthlyTwd = global, PerUserMonthlyTwd = perUser }));

    private static bool IsValid(object settings)
        => System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            settings, new System.ComponentModel.DataAnnotations.ValidationContext(settings), [], validateAllProperties: true);
}
