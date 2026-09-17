using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 用量分析的服務層（0.4.80）。
///
/// <para>
/// 有幾件事只有在服務層才看得出來：金額有沒有在記憶體加總（SQLite 的 decimal 是 TEXT，
/// 在 SQL 端排序是字典序）、分頁是不是真的分了、以及同一秒寫入的多列翻頁時會不會錯亂。
/// </para>
/// </summary>
public sealed class AiUsageAnalysisServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly AiUsageAnalysisService service;

    public AiUsageAnalysisServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();

        service = new AiUsageAnalysisService(
            context,
            LoggerFactory.Create(_ => { }).CreateLogger<AiUsageAnalysisService>());
    }

    #region 分頁

    [Fact]
    public async Task GetRecentCalls_ShouldActuallyPaginate()
    {
        // ⚠️ 全站有七支 service 的 Take 被包在 if (dataRequest.Take != 0) 裡，而呼叫端一律傳 0，
        // 所以它們**從來沒有真的分頁過**。帳本是唯一會持續成長的表，不能重蹈覆轍。
        await SeedAsync(21);

        var page = await service.GetRecentCallsAsync(Query(currentPage: 1, pageSize: 10));

        Assert.Equal(21, page.TotalCount);
        Assert.Equal(10, page.Rows.Count);
    }

    [Fact]
    public async Task GetRecentCalls_ShouldReturnEveryRowExactlyOnceAcrossPages()
    {
        // 一趟分段摘要會在**同一秒**寫進十幾列，所以「時間全部相同」是真實會發生的狀況。
        //
        // ⚠️ 誠實說明這一筆守得到什麼：它驗證翻完三頁不重不漏。但**它抓不到
        // 「漏了 ThenByDescending(Id)」**——SQLite 在排序平手時的順序是未定義的，
        // 實務上多半跟著 rowid，所以拿掉那個 tie-breaker 這筆測試仍然會過（實測過）。
        // 那個子句留著是因為它本來就正確，不是因為有測試盯著它。
        await SeedAsync(21, sameTimestamp: true);

        var seen = new List<int>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await service.GetRecentCallsAsync(Query(currentPage: page, pageSize: 10));
            seen.AddRange(result.Rows.Select(x => x.Id));
        }

        Assert.Equal(21, seen.Count);
        Assert.Equal(21, seen.Distinct().Count());
    }

    [Fact]
    public async Task GetRecentCalls_ShouldFilterByFeature()
    {
        await SeedAsync(3, feature: AiUsageFeature.AiChat);
        await SeedAsync(2, feature: AiUsageFeature.Transcription);

        var result = await service.GetRecentCallsAsync(
            Query(currentPage: 1, pageSize: 50) with { Feature = AiUsageFeature.Transcription });

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Rows, x => Assert.Equal(AiUsageFeature.Transcription, x.Feature));
    }

    #endregion

    #region 金額

    [Fact]
    public async Task Summary_ShouldSumCostsInMemory()
    {
        // ⚠️ 這一筆同時守著「不要改成 SQL 端 SUM」。SQLite 把 decimal 存成 TEXT，
        // 在資料庫端加總或排序的結果是字典序，而不是數值。
        await AddAsync(cost: 9m);
        await AddAsync(cost: 10m);

        var summary = await service.GetSummaryAsync(trendDays: 30);

        // 9 + 10 = 19。字典序會讓 "10" < "9"，若在 SQL 端排序取值就會拿到錯的東西。
        Assert.Contains("19", summary.Cards[0].Value);
    }

    [Fact]
    public async Task Summary_ShouldCountCallsWithoutUnitPrice()
    {
        // 換了 deployment 卻沒設單價時，金額是 null 而不是 0——畫面要能提示這件事，
        // 否則使用者會以為那些呼叫真的免費。
        await AddAsync(cost: 1m);
        await AddAsync(cost: null);
        await AddAsync(cost: null);

        var summary = await service.GetSummaryAsync(trendDays: 30);

        Assert.Equal(2, summary.UnpricedCallCount);
    }

    [Fact]
    public async Task Summary_ShouldCountFailuresSeparately()
    {
        await AddAsync(cost: 1m);
        await AddAsync(cost: null, outcome: AiUsageOutcome.Failed);
        await AddAsync(cost: null, outcome: AiUsageOutcome.Cancelled);

        var summary = await service.GetSummaryAsync(trendDays: 30);

        Assert.Equal(2, summary.FailedCallCount);

        // 失敗與取消沒有用量，不該被算成「未設定單價」——那是兩件不同的事。
        Assert.Equal(0, summary.UnpricedCallCount);
    }

    #endregion

    #region 起始日與空帳本

    [Fact]
    public async Task Summary_ShouldReportNoStartDate_WhenLedgerIsEmpty()
    {
        var summary = await service.GetSummaryAsync(trendDays: 30);

        Assert.Null(summary.StartedAt);
        Assert.Equal(4, summary.Cards.Count);
        Assert.Empty(summary.ByFeature);
    }

    [Fact]
    public async Task Summary_ShouldReportTheEarliestRecordAsStartDate()
    {
        await AddAsync(cost: 1m, occurredAt: DateTime.Now.AddDays(-3));
        await AddAsync(cost: 1m, occurredAt: DateTime.Now.AddDays(-1));

        var summary = await service.GetSummaryAsync(trendDays: 30);

        Assert.NotNull(summary.StartedAt);
        Assert.Equal(DateTime.Now.AddDays(-3).Date, summary.StartedAt.Value.Date);
    }

    #endregion

    #region 分佈

    [Fact]
    public async Task Summary_ShouldShowAmountTextNotRawCents()
    {
        // 圖表的 Value 是「分」（負責幾何），顯示文字必須另外給——
        // 否則畫面上會出現「1234567」這種數字。
        await AddAsync(cost: 12.34m, feature: AiUsageFeature.AiChat);

        var summary = await service.GetSummaryAsync(trendDays: 30);

        var slice = Assert.Single(summary.ByFeature);
        Assert.Equal(1234, slice.Value);
        Assert.Contains("12.34", slice.DisplayText);
    }

    [Fact]
    public async Task Summary_ShouldLabelGroupsThatHaveNoUnitPrice()
    {
        // ⚠️ 整組都沒有單價時加總會是 0，顯示成「USD 0」——那與「真的沒花錢」
        // 看起來一模一樣。這一組必須標成「未設定單價」。
        await AddAsync(cost: null, feature: AiUsageFeature.AiChat);

        var summary = await service.GetSummaryAsync(trendDays: 30);

        var slice = Assert.Single(summary.ByFeature);
        Assert.Equal("未設定單價", slice.DisplayText);
        Assert.DoesNotContain("0", slice.DisplayText);
    }

    [Fact]
    public async Task Summary_ShouldGroupUnattributedCallsTogether()
    {
        // 背景工作若沒帶使用者進來，帳會落在「（未記錄）」而不是消失。
        await AddAsync(cost: 1m, userName: "王小明");
        await AddAsync(cost: 1m, userName: null);

        var summary = await service.GetSummaryAsync(trendDays: 30);

        Assert.Contains(summary.ByUser, x => x.Label == "王小明");
        Assert.Contains(summary.ByUser, x => x.Label == "（未記錄）");
    }

    #endregion

    private static AiUsageQuery Query(int currentPage, int pageSize)
        => new(
            From: DateTime.Now.Date.AddDays(-30),
            ToExclusive: DateTime.Now.Date.AddDays(1),
            Feature: null,
            CurrentPage: currentPage,
            PageSize: pageSize);

    private async Task SeedAsync(
        int count,
        bool sameTimestamp = false,
        AiUsageFeature feature = AiUsageFeature.AiChat)
    {
        var baseTime = DateTime.Now.AddHours(-1);

        for (var index = 0; index < count; index++)
        {
            await AddAsync(
                cost: 0.01m,
                feature: feature,
                occurredAt: sameTimestamp ? baseTime : baseTime.AddSeconds(index));
        }
    }

    private async Task AddAsync(
        decimal? cost,
        AiUsageFeature feature = AiUsageFeature.AiChat,
        AiUsageOutcome outcome = AiUsageOutcome.Succeeded,
        string? userName = "王小明",
        DateTime? occurredAt = null)
    {
        context.AiUsageLog.Add(new AiUsageLog
        {
            OccurredAt = occurredAt ?? DateTime.Now,
            Feature = feature,
            Outcome = outcome,
            Provider = "AzureOpenAI",
            Model = "gpt-4o-mini",
            InputTokens = outcome == AiUsageOutcome.Succeeded ? 100 : null,
            OutputTokens = outcome == AiUsageOutcome.Succeeded ? 50 : null,
            EstimatedCost = cost,
            Currency = "USD",
            UserName = userName,
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }
}
