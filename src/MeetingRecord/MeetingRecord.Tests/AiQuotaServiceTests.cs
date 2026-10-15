using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 用量上限（1.0.29，移植自 Starter 的 AiQuotaTests，只留每月上限）。
/// 金額以每一列自己的匯率換算；未定價算 0；讀取失敗時放行。
/// </summary>
public sealed class AiQuotaServiceTests : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0);

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;
    private readonly StaticOptionsMonitor<AiQuotaSettings> settings = new(new AiQuotaSettings());
    private readonly ExchangeRateCache rates = new();

    public AiQuotaServiceTests()
    {
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }

    private AiQuotaService CreateService()
        => new(context, settings, rates, new FixedTimeProvider(Now), NullLogger<AiQuotaService>.Instance);

    private async Task AddUsageAsync(decimal? costUsd, decimal? rate, int? userId = 1, DateTime? occurredAt = null)
    {
        context.AiUsageLog.Add(new AiUsageLog
        {
            OccurredAt = occurredAt ?? Now.AddHours(-1),
            Feature = AiUsageFeature.AiChat,
            Outcome = AiUsageOutcome.Succeeded,
            Provider = "AzureOpenAI",
            Model = "gpt-5.6-sol",
            EstimatedCost = costUsd,
            ExchangeRate = rate,
            UserId = userId,
            UserName = userId is null ? null : $"使用者{userId}",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Unlimited_ShouldNeverBlock()
    {
        await AddUsageAsync(1000m, 32m);

        Assert.Null(await CreateService().CheckAsync(1));
    }

    [Fact]
    public async Task GlobalLimit_ShouldUseEachRowsOwnRate()
    {
        settings.CurrentValue = new AiQuotaSettings { GlobalMonthlyTwd = 100 };
        await AddUsageAsync(2m, 30m);    // 60 元
        await AddUsageAsync(1m, 40m);    // 40 元 → 合計 100，剛好達上限

        var blocked = await CreateService().CheckAsync(userId: 9);

        Assert.NotNull(blocked);
        Assert.Equal(AiQuotaScope.Global, blocked!.Scope);
        Assert.Equal(100m, blocked.Used);
        Assert.Equal(new DateTime(2026, 11, 1), blocked.ResetAt);
    }

    [Fact]
    public async Task PerUserLimit_ShouldOnlyCountThatUser()
    {
        settings.CurrentValue = new AiQuotaSettings { PerUserMonthlyTwd = 50 };
        await AddUsageAsync(2m, 30m, userId: 1);   // 使用者 1：60 元
        await AddUsageAsync(1m, 30m, userId: 2);   // 使用者 2：30 元

        var service = CreateService();

        Assert.Equal(AiQuotaScope.User, (await service.CheckAsync(1))!.Scope);
        Assert.Null(await service.CheckAsync(2));
    }

    [Fact]
    public async Task UnpricedRows_ShouldCountAsZero()
    {
        settings.CurrentValue = new AiQuotaSettings { GlobalMonthlyTwd = 10 };
        await AddUsageAsync(null, 32m);
        await AddUsageAsync(null, null);

        Assert.Null(await CreateService().CheckAsync(1));
    }

    [Fact]
    public async Task RowWithoutRate_ShouldFallBackToTheCurrentRate()
    {
        // 寧可多算，不要少算：沒有匯率快照的列用目前匯率換算。
        settings.CurrentValue = new AiQuotaSettings { GlobalMonthlyTwd = 30 };
        rates.Set(new ExchangeRateSnapshot("USD", "TWD", 32m, Now));
        await AddUsageAsync(1m, null);

        Assert.NotNull(await CreateService().CheckAsync(1));
    }

    [Fact]
    public async Task LastMonthRows_ShouldNotCount()
    {
        settings.CurrentValue = new AiQuotaSettings { GlobalMonthlyTwd = 10 };
        await AddUsageAsync(100m, 32m, occurredAt: new DateTime(2026, 9, 30, 23, 59, 0));

        Assert.Null(await CreateService().CheckAsync(1));
    }

    [Fact]
    public async Task ReadFailure_ShouldAllowTheCall()
    {
        settings.CurrentValue = new AiQuotaSettings { GlobalMonthlyTwd = 10 };
        await context.Database.ExecuteSqlRawAsync("DROP TABLE \"AiUsageLog\"");

        Assert.Null(await CreateService().CheckAsync(1));
    }

    [Fact]
    public async Task Status_ShouldFlagEightyPercentAsWarning()
    {
        settings.CurrentValue = new AiQuotaSettings { GlobalMonthlyTwd = 100 };
        await AddUsageAsync(2.5m, 32m);   // 80 元

        var global = Assert.Single(await CreateService().GetStatusAsync(null));

        Assert.True(global.IsWarning);
        Assert.False(global.IsReached);
        Assert.Equal("全系統每月", global.Label);
    }

    [Fact]
    public async Task TopUsers_ShouldOrderByUsage()
    {
        await AddUsageAsync(1m, 30m, userId: 1);
        await AddUsageAsync(3m, 30m, userId: 2);
        await AddUsageAsync(1m, 30m, userId: null);   // 背景工作沒有歸屬：不列入

        var top = await CreateService().GetTopUsersAsync(10);

        Assert.Equal([2, 1], top.Select(x => x.UserId));
        Assert.Equal("使用者2", top[0].UserName);
    }

    [Fact]
    public void ExceededMessage_ShouldTellWhenItResets()
    {
        var message = AiQuotaExceededException.BuildMessage(FakeAiQuotaService.GlobalReached(5000, 5012.4m));

        Assert.Contains("全系統每月 5,000 元", message);
        Assert.Contains("11/1 起恢復", message);
    }

    private sealed class FixedTimeProvider(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
