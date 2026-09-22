using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;
using Microsoft.Extensions.Options;
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

    /// <summary>匯率快取。具體類別而不是介面，測試直接 Set 一個匯率進去即可。</summary>
    private readonly ExchangeRateCache exchangeRates = new();

    public AiUsageAnalysisServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(connection)
            .Options);
        context.Database.EnsureCreated();

        service = BuildService(new ExchangeRateSettings());
    }

    /// <summary>用指定的匯率設定建一支 service（預設那支是啟用換算的）。</summary>
    private AiUsageAnalysisService BuildService(ExchangeRateSettings settings)
        => new(
            context,
            exchangeRates,
            Options.Create(settings),
            LoggerFactory.Create(_ => { }).CreateLogger<AiUsageAnalysisService>());

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

        var summary = await service.GetSummaryAsync();

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

        var summary = await service.GetSummaryAsync();

        Assert.Equal(2, summary.UnpricedCallCount);
    }

    [Fact]
    public async Task Summary_ShouldCountFailuresSeparately()
    {
        await AddAsync(cost: 1m);
        await AddAsync(cost: null, outcome: AiUsageOutcome.Failed);
        await AddAsync(cost: null, outcome: AiUsageOutcome.Cancelled);

        var summary = await service.GetSummaryAsync();

        Assert.Equal(2, summary.FailedCallCount);

        // 失敗與取消沒有用量，不該被算成「未設定單價」——那是兩件不同的事。
        Assert.Equal(0, summary.UnpricedCallCount);
    }

    #endregion

    #region 起始日與空帳本

    [Fact]
    public async Task Summary_ShouldReportNoStartDate_WhenLedgerIsEmpty()
    {
        var summary = await service.GetSummaryAsync();

        Assert.Null(summary.StartedAt);
        Assert.Equal(4, summary.Cards.Count);
        Assert.Empty(summary.ByFeature);
    }

    [Fact]
    public async Task Summary_ShouldReportTheEarliestRecordAsStartDate()
    {
        await AddAsync(cost: 1m, occurredAt: DateTime.Now.AddDays(-3));
        await AddAsync(cost: 1m, occurredAt: DateTime.Now.AddDays(-1));

        var summary = await service.GetSummaryAsync();

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

        var summary = await service.GetSummaryAsync();

        var slice = Assert.Single(summary.ByFeature);
        Assert.Equal(1234, slice.Value);
        Assert.Contains("12.34", slice.DisplayText);
    }

    [Fact]
    public async Task Summary_ShouldLabelGroupsThatHaveNoUnitPrice()
    {
        // ⚠️ 整組都算不出金額時加總會是 0，顯示成「NT$ 0」——那與「真的沒花錢」
        // 看起來一模一樣。這一組必須被標示出來。0.4.88 起「算不出來」多了一種原因：有單價但沒匯率。
        await AddAsync(cost: null, feature: AiUsageFeature.AiChat);

        var summary = await service.GetSummaryAsync();

        var slice = Assert.Single(summary.ByFeature);
        Assert.Equal("未設定單價或無匯率", slice.DisplayText);
        Assert.DoesNotContain("0", slice.DisplayText);
    }

    [Fact]
    public async Task Summary_ShouldGroupUnattributedCallsTogether()
    {
        // 背景工作若沒帶使用者進來，帳會落在「（未記錄）」而不是消失。
        await AddAsync(cost: 1m, userName: "王小明");
        await AddAsync(cost: 1m, userName: null);

        var summary = await service.GetSummaryAsync();

        Assert.Contains(summary.ByUser, x => x.Label == "王小明");
        Assert.Contains(summary.ByUser, x => x.Label == "（未記錄）");
    }

    #endregion

    #region 功能篩選（0.4.91）

    [Fact]
    public async Task Summary_WithFeature_CardsShouldOnlyCountThatFeature()
    {
        // ⭐ 0.4.91 之前功能下拉只接到明細表，卡片永遠是全部功能——選了下拉，上面一個數字都不會動。
        await AddAsync(cost: 2m, feature: AiUsageFeature.AiChat);
        await AddAsync(cost: 3m, feature: AiUsageFeature.AiChat);
        await AddAsync(cost: 50m, feature: AiUsageFeature.MeetingDraft);

        var all = await service.GetSummaryAsync();
        var chat = await service.GetSummaryAsync(AiUsageFeature.AiChat);

        Assert.Contains("55", all.Cards[0].Value);
        Assert.Contains("5", chat.Cards[0].Value);
        Assert.DoesNotContain("55", chat.Cards[0].Value);

        // 呼叫次數卡片
        Assert.Equal("3", all.Cards[3].Value);
        Assert.Equal("2", chat.Cards[3].Value);
    }

    [Fact]
    public async Task Summary_WithFeature_DistributionsShouldOnlyCountThatFeature()
    {
        await AddAsync(cost: 1m, feature: AiUsageFeature.AiChat, userName: "問答的人");
        await AddAsync(cost: 1m, feature: AiUsageFeature.MeetingDraft, userName: "生成的人");

        var chat = await service.GetSummaryAsync(AiUsageFeature.AiChat);

        Assert.Equal("問答的人", Assert.Single(chat.ByUser).Label);
    }

    [Fact]
    public async Task Summary_WithFeature_WarningCountsShouldOnlyCountThatFeature()
    {
        // 提示條講的是「你正在看的這個功能」有幾筆沒算進去，不是全站。
        await AddAsync(cost: null, feature: AiUsageFeature.AiChat);
        await AddAsync(cost: null, feature: AiUsageFeature.MeetingDraft);
        await AddAsync(cost: null, feature: AiUsageFeature.MeetingDraft);

        var chat = await service.GetSummaryAsync(AiUsageFeature.AiChat);

        Assert.Equal(1, chat.UnpricedCallCount);
    }

    [Fact]
    public async Task Summary_WithFeature_ShouldKeepTheLedgerStartDate()
    {
        // ⭐ 統計起始日講的是「帳本從哪天開始記」，不是「這個功能第一次被用是哪天」。
        // 套了篩選的話選待辦擷取會顯示比較晚的日期，使用者會以為之前的紀錄遺失了。
        await AddAsync(cost: 1m, feature: AiUsageFeature.MeetingDraft, occurredAt: DateTime.Now.AddDays(-5));
        await AddAsync(cost: 1m, feature: AiUsageFeature.TodoExtraction, occurredAt: DateTime.Now.AddDays(-1));

        var todo = await service.GetSummaryAsync(AiUsageFeature.TodoExtraction);

        Assert.Equal(DateTime.Now.AddDays(-5).Date, todo.StartedAt?.Date);
    }

    [Fact]
    public async Task Summary_Transcription_TokenCardShouldBeDash()
    {
        // 「語音轉錄的 token 0 / 0」數字沒錯，但會讓人以為這個月沒用——其實是不以 token 計費。
        await AddAsync(cost: 1m, feature: AiUsageFeature.Transcription);

        var summary = await service.GetSummaryAsync(AiUsageFeature.Transcription);

        Assert.Equal("—", summary.Cards[1].Value);
        Assert.NotEqual("—", summary.Cards[2].Value);
    }

    [Fact]
    public async Task Summary_TokenFeature_AudioCardShouldBeDash()
    {
        await AddAsync(cost: 1m, feature: AiUsageFeature.AiChat);

        var summary = await service.GetSummaryAsync(AiUsageFeature.AiChat);

        Assert.Equal("—", summary.Cards[2].Value);
        Assert.NotEqual("—", summary.Cards[1].Value);
    }

    [Fact]
    public async Task Summary_AllFeatures_ShouldShowBothUnitCards()
    {
        // 全部功能時兩張卡都要有數字，不可以因為上面那條規則誤傷。
        await AddAsync(cost: 1m, feature: AiUsageFeature.AiChat);

        var summary = await service.GetSummaryAsync();

        Assert.NotEqual("—", summary.Cards[1].Value);
        Assert.NotEqual("—", summary.Cards[2].Value);
    }

    [Fact]
    public async Task Summary_CumulativeLastPoint_ShouldMatchTheMonthCard()
    {
        // 曲線是第一張卡的趨勢版：最後一點的金額（分）必須等於卡片上的本月金額。
        await AddAsync(cost: 1m, exchangeRate: 30m);
        await AddAsync(cost: 2m, exchangeRate: 30m);

        var summary = await service.GetSummaryAsync();

        Assert.Equal(9000, summary.CumulativeCost[^1].Created);   // (1+2) × 30 = NT$90 → 9000 分
        Assert.Contains("90", summary.Cards[0].Value);
    }

    #endregion

    #region 台幣換算（0.4.88）

    [Fact]
    public async Task Summary_ShouldConvertUsingEachRowsOwnRate()
    {
        exchangeRates.Set(new ExchangeRateSnapshot("USD", "TWD", 31.863639m, DateTime.Now));

        // 兩列金額相同但匯率不同——換算必須各用各的，而不是一律套今天的匯率。
        await AddAsync(cost: 1m, exchangeRate: 30m);
        await AddAsync(cost: 1m, exchangeRate: 32m);

        var summary = await service.GetSummaryAsync();

        Assert.Contains("62", summary.Cards[0].Value);
        Assert.Contains("NT$", summary.Cards[0].Value);
    }

    [Fact]
    public async Task Summary_ShouldExcludeRowsWithoutRateAndCountThem()
    {
        // 0.4.88 之前的舊紀錄有金額但沒有匯率。它們不可以被當成 0 混進總額，
        // 也不可以默默消失——畫面要能提示「有 N 筆沒算進去」。
        await AddAsync(cost: 2m, exchangeRate: 30m);
        await AddAsync(cost: 5m, exchangeRate: null);

        var summary = await service.GetSummaryAsync();

        Assert.Contains("60", summary.Cards[0].Value);
        Assert.Equal(1, summary.NoRateCallCount);
    }

    [Fact]
    public async Task Summary_MonthOverMonth_ShouldUseSourceCurrencyNotConverted()
    {
        // ⭐ 整組最重要的一條。台幣的月比月 = 用量變化 × 匯率變化，
        // 匯率動 10% 會在卡片上顯示成「花費增加 10%」，而畫面上沒有任何線索指出那是匯率。
        // 比較的必須是**實際花掉的錢**（定價幣別），顯示才用台幣。
        var now = DateTime.Now;

        // ⚠️ 直接用 now.AddMonths(-1) 會剛好落在 previousToExclusive 上而被排除（比較是 <）。
        //    用同一支區間函式算出視窗中點，測試才不會依今天幾號而時好時壞。
        var (currentFrom, previousFrom, previousToExclusive) = AiUsageMetrics.BuildMonthToDateRanges(now);
        var lastMonthMidpoint = previousFrom.AddTicks((previousToExclusive - previousFrom).Ticks / 2);

        await AddAsync(cost: 1m, exchangeRate: 30m, occurredAt: currentFrom.AddTicks((now - currentFrom).Ticks / 2));
        await AddAsync(cost: 1m, exchangeRate: 33m, occurredAt: lastMonthMidpoint);

        var summary = await service.GetSummaryAsync();

        // 美金花費一模一樣 ⇒ 變化率必須是 0%，不可以因為匯率差 10% 而變成 -10%。
        Assert.Contains("0%", summary.Cards[0].Caption);
    }

    [Fact]
    public async Task Summary_WhenConversionDisabled_ShouldFallBackToSourceCurrency()
    {
        // ⚠️ 關閉換算時整頁退回定價幣別。「有匯率顯示台幣、沒有顯示美金」
        // 會讓總額變成兩種幣別相加——那不是降級，是錯的數字。
        await AddAsync(cost: 9m, exchangeRate: 30m);
        await AddAsync(cost: 10m, exchangeRate: null);

        var disabled = BuildService(new ExchangeRateSettings { Enabled = false });
        var summary = await disabled.GetSummaryAsync();

        Assert.Contains("19", summary.Cards[0].Value);
        Assert.Contains("USD", summary.Cards[0].Value);

        // 沒在換算，就不該跳「有 N 筆沒有匯率」的提示。
        Assert.Equal(0, summary.NoRateCallCount);
        Assert.Null(summary.ExchangeRateNote);
    }

    [Fact]
    public async Task Summary_ShouldReportTheRateInUse()
    {
        exchangeRates.Set(new ExchangeRateSnapshot("USD", "TWD", 31.863639m, DateTime.Now));
        await AddAsync(cost: 1m, exchangeRate: 31.863639m);

        var summary = await service.GetSummaryAsync();

        Assert.NotNull(summary.ExchangeRateNote);
        Assert.Contains("31.8636", summary.ExchangeRateNote);
        Assert.Contains("TWD", summary.ExchangeRateNote);
    }

    [Fact]
    public async Task RecentCalls_ShouldConvertTheCostColumn()
    {
        await AddAsync(cost: 2m, exchangeRate: 30m);

        var page = await service.GetRecentCallsAsync(Query(currentPage: 1, pageSize: 10));

        var row = Assert.Single(page.Rows);
        Assert.Equal("NT$ 60.00", row.CostText);
    }

    [Fact]
    public async Task RecentCalls_WithoutRate_ShouldShowDash()
    {
        // 有金額但換不出來時顯示「—」，不可以退回顯示美金金額——
        // 同一欄裡混兩種幣別會讓人把數字看錯三十幾倍。
        await AddAsync(cost: 2m, exchangeRate: null);

        var page = await service.GetRecentCallsAsync(Query(currentPage: 1, pageSize: 10));

        Assert.Equal("—", Assert.Single(page.Rows).CostText);
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

    /// <param name="exchangeRate">
    /// 0.4.88 起金額會乘上這一列自己的匯率。預設 1 讓既有測試維持「金額原樣顯示」的語意，
    /// 專測換算的那幾筆才會傳別的值。**傳 null 代表這一列沒有匯率**（0.4.88 之前的舊紀錄）。
    /// </param>
    private async Task AddAsync(
        decimal? cost,
        AiUsageFeature feature = AiUsageFeature.AiChat,
        AiUsageOutcome outcome = AiUsageOutcome.Succeeded,
        string? userName = "王小明",
        DateTime? occurredAt = null,
        decimal? exchangeRate = 1m)
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
            ExchangeRate = exchangeRate,
            ConvertedCurrency = exchangeRate is null ? null : "TWD",
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
