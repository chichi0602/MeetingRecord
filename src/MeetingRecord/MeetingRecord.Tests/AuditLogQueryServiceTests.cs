using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 稽核紀錄頁的查詢與清除（1.0.32）。⚠️ 稽核紀錄存 UTC、畫面用本地時間：篩選、顯示、清除門檻三個轉換點各有測試。
/// </summary>
public sealed class AuditLogQueryServiceTests : IAsyncDisposable
{
    private static readonly DateTime BaseUtc = new(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection = new("Data Source=:memory:");

    public AuditLogQueryServiceTests()
    {
        connection.Open();
        using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    [Fact]
    public async Task GetAsync_ShouldReturnNewestFirst_WithIdAsTieBreaker()
    {
        await SeedAsync(
            Row(BaseUtc, AuditActions.LoginSuccess),
            Row(BaseUtc.AddMinutes(5), AuditActions.UserCreate),
            Row(BaseUtc.AddMinutes(5), AuditActions.UserUpdate));

        var result = await CreateService().GetAsync(new AuditLogQuery { PageSize = 10 });

        Assert.Equal(3, result.Count);
        Assert.Equal([AuditActions.UserUpdate, AuditActions.UserCreate, AuditActions.LoginSuccess], result.Result.Select(x => x.Action));
    }

    [Fact]
    public async Task GetAsync_SortAscending_ShouldReturnOldestFirst()
    {
        await SeedAsync(Row(BaseUtc, AuditActions.LoginSuccess), Row(BaseUtc.AddMinutes(1), AuditActions.UserCreate));

        var result = await CreateService().GetAsync(new AuditLogQuery { PageSize = 10, SortDescending = false });

        Assert.Equal([AuditActions.LoginSuccess, AuditActions.UserCreate], result.Result.Select(x => x.Action));
    }

    [Fact]
    public async Task GetAsync_ShouldPageWithoutOverlap()
    {
        await SeedAsync(Enumerable.Range(0, 7).Select(i => Row(BaseUtc.AddMinutes(i), $"Test.Row{i}")).ToArray());
        var service = CreateService();

        var page1 = await service.GetAsync(new AuditLogQuery { CurrentPage = 1, PageSize = 3 });
        var page3 = await service.GetAsync(new AuditLogQuery { CurrentPage = 3, PageSize = 3 });

        Assert.Equal(7, page1.Count);
        Assert.Equal(["Test.Row6", "Test.Row5", "Test.Row4"], page1.Result.Select(x => x.Action));
        Assert.Equal(["Test.Row0"], page3.Result.Select(x => x.Action));
    }

    /// <summary>顯示的轉換點：資料庫的 UTC → 本地時間；另外保留 UTC 原值給明細窗。</summary>
    [Fact]
    public async Task GetAsync_ShouldConvertOccurredAtToLocalTime()
    {
        await SeedAsync(Row(BaseUtc, AuditActions.LoginSuccess));

        var row = (await CreateService().GetAsync(new AuditLogQuery())).Result.Single();

        Assert.Equal(BaseUtc.ToLocalTime(), row.OccurredAt);
        Assert.Equal(BaseUtc, row.OccurredAtUtc);
        Assert.Equal(DateTimeKind.Utc, row.OccurredAtUtc.Kind);
    }

    /// <summary>篩選的轉換點：畫面輸入的本地時間要先換成 UTC 才和資料庫比。</summary>
    [Fact]
    public async Task Filter_TimeRange_ShouldTreatInputAsLocalTime()
    {
        await SeedAsync(Row(BaseUtc, AuditActions.LoginSuccess));
        var service = CreateService();
        var local = BaseUtc.ToLocalTime();

        var inclusive = await service.GetAsync(new AuditLogQuery { StartTime = DateTime.SpecifyKind(local, DateTimeKind.Unspecified), EndTime = DateTime.SpecifyKind(local, DateTimeKind.Unspecified) });
        var afterIt = await service.GetAsync(new AuditLogQuery { StartTime = DateTime.SpecifyKind(local.AddSeconds(1), DateTimeKind.Unspecified) });
        var beforeIt = await service.GetAsync(new AuditLogQuery { EndTime = DateTime.SpecifyKind(local.AddSeconds(-1), DateTimeKind.Unspecified) });

        Assert.Equal(1, inclusive.Count);
        Assert.Equal(0, afterIt.Count);
        Assert.Equal(0, beforeIt.Count);
    }

    [Fact]
    public async Task Filter_ActionAndResult_ShouldMatchExactly()
    {
        await SeedAsync(
            Row(BaseUtc, AuditActions.LoginSuccess),
            Row(BaseUtc, AuditActions.LoginFailed, success: false),
            Row(BaseUtc, AuditActions.LoginLockedOut, success: false));
        var service = CreateService();

        var byAction = await service.GetAsync(new AuditLogQuery { Action = AuditActions.LoginFailed });
        var failures = await service.GetAsync(new AuditLogQuery { Success = false });

        Assert.Equal([AuditActions.LoginFailed], byAction.Result.Select(x => x.Action));
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public async Task Filter_ActorAccount_ShouldMatchPartially()
    {
        await SeedAsync(
            Row(BaseUtc, AuditActions.LoginSuccess, account: "alice.chen"),
            Row(BaseUtc, AuditActions.LoginSuccess, account: "bob"),
            Row(BaseUtc, AuditActions.AuditAutoPurge, account: null));

        var result = await CreateService().GetAsync(new AuditLogQuery { ActorAccount = " alice " });

        Assert.Equal(["alice.chen"], result.Result.Select(x => x.ActorAccount));
    }

    /// <summary>IP 是前綴比對：「10.0.」不能比到「110.0.0.1」。</summary>
    [Fact]
    public async Task Filter_ClientIp_ShouldMatchPrefixOnly()
    {
        await SeedAsync(
            Row(BaseUtc, AuditActions.LoginSuccess, ip: "10.0.0.1"),
            Row(BaseUtc, AuditActions.LoginSuccess, ip: "110.0.0.1"),
            Row(BaseUtc, AuditActions.LoginSuccess, ip: null));

        var result = await CreateService().GetAsync(new AuditLogQuery { ClientIp = "10.0." });

        Assert.Equal(["10.0.0.1"], result.Result.Select(x => x.ClientIp));
    }

    [Fact]
    public async Task Filter_Keyword_ShouldSearchTargetDetailAndIp()
    {
        await SeedAsync(
            Row(BaseUtc, AuditActions.UserCreate, targetId: "42"),
            Row(BaseUtc, AuditActions.RoleUpdate, detail: "name=審核者"),
            Row(BaseUtc, AuditActions.LoginSuccess, ip: "192.168.1.42"),
            Row(BaseUtc, AuditActions.LoginFailed));
        var service = CreateService();

        Assert.Equal(2, (await service.GetAsync(new AuditLogQuery { Keyword = "42" })).Count);
        Assert.Equal([AuditActions.RoleUpdate], (await service.GetAsync(new AuditLogQuery { Keyword = "審核" })).Result.Select(x => x.Action));
    }

    [Fact]
    public async Task GetForExportAsync_ShouldReturnAllFilteredRowsNewestFirst()
    {
        await SeedAsync(Enumerable.Range(0, 12).Select(i => Row(BaseUtc.AddMinutes(i), AuditActions.LoginSuccess)).ToArray());

        var rows = await CreateService().GetForExportAsync(new AuditLogQuery { PageSize = 3 });

        Assert.Equal(12, rows.Count);
        Assert.True(rows[0].OccurredAt > rows[^1].OccurredAt);
    }

    [Fact]
    public async Task GetDistinctActionsAsync_ShouldReturnSortedDistinctCodes_IncludingUnknownOnes()
    {
        await SeedAsync(
            Row(BaseUtc, AuditActions.UserCreate),
            Row(BaseUtc, AuditActions.LoginSuccess),
            Row(BaseUtc, AuditActions.LoginSuccess),
            Row(BaseUtc, "Legacy.Retired"));

        var actions = await CreateService().GetDistinctActionsAsync();

        Assert.Equal(["Legacy.Retired", AuditActions.LoginSuccess, AuditActions.UserCreate], actions);
    }

    [Fact]
    public async Task PurgeBeforeAsync_ShouldDeleteOnlyStrictlyOlderRows()
    {
        await SeedAsync(Row(BaseUtc.AddSeconds(-1), "Test.Old"), Row(BaseUtc, "Test.Boundary"), Row(BaseUtc.AddDays(1), "Test.New"));

        var removed = await CreateService().PurgeBeforeAsync(BaseUtc);

        Assert.Equal(1, removed);
        Assert.Equal(["Test.Boundary", "Test.New"], await RemainingActionsAsync());
    }

    /// <summary>清除門檻的轉換點：「N 天前」以 UTC 現在時間計算（資料庫存 UTC）。</summary>
    [Fact]
    public async Task PurgeAsync_ShouldUseUtcNowAsTheThreshold()
    {
        var utcNow = DateTime.UtcNow;
        await SeedAsync(Row(utcNow.AddDays(-30).AddHours(-1), "Test.Expired"), Row(utcNow.AddDays(-30).AddHours(1), "Test.Kept"));

        var result = await CreateService().PurgeAsync(30);

        Assert.True(result.Success);
        Assert.Equal(["Test.Kept"], await RemainingActionsAsync());
    }

    [Fact]
    public async Task ClearAllAsync_ShouldDeleteEverything()
    {
        await SeedAsync(Row(BaseUtc, AuditActions.LoginSuccess), Row(BaseUtc, AuditActions.UserCreate));

        var result = await CreateService().ClearAllAsync();

        Assert.True(result.Success);
        Assert.Empty(await RemainingActionsAsync());
    }

    [Fact]
    public void AdapterModel_ShouldDescribeActorTargetAndAction()
    {
        var system = new MeetingRecord.Models.AdapterModel.AuditLogAdapterModel { Action = AuditActions.AuditAutoPurge, TargetType = "AuditLog", TargetId = "*" };
        var user = new MeetingRecord.Models.AdapterModel.AuditLogAdapterModel { Action = "Legacy.Retired", ActorUserId = 3, ActorAccount = "alice" };

        Assert.Equal("（系統／匿名）", system.ActorDisplay);
        Assert.Equal("AuditLog#*", system.TargetDisplay);
        Assert.Equal("自動清除稽核紀錄", system.ActionDisplayName);
        Assert.Equal("Audit", system.ActionCategory);
        Assert.Equal("alice（Id=3）", user.ActorDisplay);
        Assert.Equal("—", user.TargetDisplay);
        Assert.Equal("Legacy.Retired", user.ActionDisplayName);
    }

    private AuditLogQueryService CreateService()
        => new(new TestDbContextFactory(connection).CreateDbContext(), NullLogger<AuditLogQueryService>.Instance);

    private static AuditLog Row(
        DateTime occurredAtUtc,
        string action,
        bool success = true,
        string? account = "alice",
        string? ip = null,
        string? targetId = null,
        string? detail = null) => new()
        {
            OccurredAt = occurredAtUtc,
            Action = action,
            Success = success,
            ActorUserId = account is null ? null : 1,
            ActorAccount = account,
            TargetType = targetId is null ? null : "MyUser",
            TargetId = targetId,
            Detail = detail,
            ClientIp = ip,
        };

    private async Task SeedAsync(params AuditLog[] rows)
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.AuditLog.AddRange(rows);
        await context.SaveChangesAsync();
    }

    private async Task<List<string>> RemainingActionsAsync()
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        return await context.AuditLog.OrderBy(x => x.Action).Select(x => x.Action).ToListAsync();
    }
}
