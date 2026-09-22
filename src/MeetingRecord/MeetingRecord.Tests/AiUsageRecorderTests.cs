using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 記帳路徑（0.4.88 補上）。
///
/// <para>
/// ⭐ 這裡釘的是整個匯率功能的第一不變量：<b>抓不到匯率不可以讓記帳失敗。</b>
/// <c>RecordAsync</c> 跑在使用者按下「產生會議紀錄」之後、串流結束之前，
/// 而匯率是一個會斷線的外部服務。它缺席時該筆只是沒有台幣金額，
/// 絕不可以連帶讓 token 用量也記不進去——那等於用「看不到花多少錢」換「看不到用了多少」。
/// </para>
/// </summary>
public sealed class AiUsageRecorderTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;

    public AiUsageRecorderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();
    }

    private AiUsageRecorder BuildRecorder(ExchangeRateCache rates)
        => new(
            context,
            Options.Create(new LlmSettings
            {
                Currency = "USD",
                Pricing = new Dictionary<string, LlmPricingSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gpt-5.6-sol"] = new() { InputPerMillionTokens = 4m, OutputPerMillionTokens = 20m },
                },
            }),
            rates,
            LoggerFactory.Create(_ => { }).CreateLogger<AiUsageRecorder>());

    private static AiUsageEntry Entry() => new(
        AiUsageFeature.AiChat,
        AiUsageOutcome.Succeeded,
        Provider: "AzureOpenAI",
        Model: "gpt-5.6-sol",
        // 輸入 1M × 4 + 輸出 0.1M × 20 = 6 美金，剛好是整數，斷言才不會被小數雜訊干擾。
        Tokens: new AiTokenUsage(1_000_000, 100_000));

    [Fact]
    public async Task RecordAsync_WithoutExchangeRate_ShouldStillWriteTheRow()
    {
        // ⭐ 匯率服務從來沒抓成功過（斷網、剛重啟、對方掛掉）。
        await BuildRecorder(new ExchangeRateCache()).RecordAsync(Entry());

        var row = Assert.Single(await context.AiUsageLog.AsNoTracking().ToListAsync());

        // 金額照算（那是單價的事，與匯率無關），只有匯率欄位留空。
        Assert.Equal(6m, row.EstimatedCost);
        Assert.Equal("USD", row.Currency);
        Assert.Null(row.ExchangeRate);
        Assert.Null(row.ConvertedCurrency);
    }

    [Fact]
    public async Task RecordAsync_WithExchangeRate_ShouldSnapshotIt()
    {
        var rates = new ExchangeRateCache();
        rates.Set(new ExchangeRateSnapshot("USD", "TWD", 31.863639m, DateTime.Now));

        await BuildRecorder(rates).RecordAsync(Entry());

        var row = Assert.Single(await context.AiUsageLog.AsNoTracking().ToListAsync());

        // 匯率是**寫入當下的快照**，不是讀取時才查——否則今天匯率一動，去年的帳就整批跟著變。
        Assert.Equal(31.863639m, row.ExchangeRate);
        Assert.Equal("TWD", row.ConvertedCurrency);
    }

    [Fact]
    public async Task RecordAsync_BaseCurrencyMismatch_ShouldLeaveTheRateBlank()
    {
        // ⭐ 有人把 LlmSettings.Currency 改成 EUR，卻沒改匯率來源。
        // 得到空白（畫面顯示「—」）是對的；照樣乘上去會得到錯好幾 % 卻看起來很正常的金額。
        var rates = new ExchangeRateCache();
        rates.Set(new ExchangeRateSnapshot("EUR", "TWD", 34.5m, DateTime.Now));

        await BuildRecorder(rates).RecordAsync(Entry());

        var row = Assert.Single(await context.AiUsageLog.AsNoTracking().ToListAsync());

        Assert.Null(row.ExchangeRate);
        Assert.Null(row.ConvertedCurrency);
        Assert.Equal(6m, row.EstimatedCost);
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }
}
