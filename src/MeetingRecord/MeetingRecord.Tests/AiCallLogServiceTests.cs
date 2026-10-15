using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

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
