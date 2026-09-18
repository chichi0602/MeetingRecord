using MeetingRecord.Web.Components.Views.Todos;

namespace MeetingRecord.Tests;

/// <summary>
/// 待辦頁「列數剛好吃滿一個畫面」的決策（0.4.86）。
///
/// <para>
/// 高度只有 DOM 量得到，所以量測在 <c>wwwroot/js/todo-auto-fit.js</c>；
/// 但一旦把決策也放進 JS，就再也沒有任何自動化驗證擋得住它——本專案沒有 bUnit。
/// 這組測試守兩條線：**不可以留著捲軸**，以及**不可以在兩個列數之間來回跳**。
/// </para>
/// </summary>
public sealed class TodoAutoFitTests
{
    // 實測值：待辦頁的列高不是固定的，截止日欄會把「逾期 N 天」折到第二行。
    private const double ShortRow = 63.75;
    private const double TallRow = 81;
    private const double Width = 1326;
    private const double Height = 910;

    private static TodoFitSample Sample(
        double leftover,
        double tallest = ShortRow,
        double shortest = ShortRow,
        double width = Width,
        double height = Height)
        => new(leftover, tallest, shortest, width, height);

    private static TodoFitDecision First(int current, TodoFitSample sample)
        => TodoAutoFit.Decide(current, sample, TodoAutoFit.InitialState);

    #region 補列

    [Fact]
    public void Decide_LeftoverFitsAnotherRow_ShouldGrow()
    {
        // 這一條就是使用者回報的症狀：表格底離畫面底還有一整列的空間，卻沒補上去。
        var decision = First(8, Sample(leftover: 64));

        Assert.Equal(9, decision.PageSize);
    }

    [Fact]
    public void Decide_LotsOfLeftover_ShouldGrowInOneStep()
    {
        // 從回退值一路補到滿，不要一次補一列——每補一列就是一輪查詢與一次重繪。
        var decision = First(6, Sample(leftover: 282));

        Assert.Equal(6 + 4, decision.PageSize);
    }

    [Fact]
    public void Decide_LeftoverSmallerThanShortestRow_ShouldStay()
    {
        // 剩下的空間連最矮的一列都放不下，就是已經吃滿了。那點留白是排不掉的。
        var decision = First(11, Sample(leftover: 62));

        Assert.Equal(11, decision.PageSize);
    }

    [Fact]
    public void Decide_GrowsByTallestRow_NotShortest()
    {
        // 補列用最高的列估：估太樂觀會補到超出、再縮回來，使用者會看到捲軸閃一下。
        // 剩 130px，用最矮的（63.75）會補 2 列，用最高的（81）只補 1 列。
        var decision = First(5, Sample(leftover: 130, tallest: TallRow, shortest: ShortRow));

        Assert.Equal(6, decision.PageSize);
    }

    #endregion

    #region 縮列

    [Fact]
    public void Decide_Overflowing_ShouldShrink()
    {
        // 只超出一點點（不到一列）就拿掉一列。
        var decision = First(10, Sample(leftover: -20));

        Assert.Equal(9, decision.PageSize);
    }

    [Fact]
    public void Decide_Overflowing_ShouldAssumeTheRemovedRowIsTheShortest()
    {
        // 超出 70px 而最矮的列是 63.75px：只拿掉一列仍然會剩 6px 的捲軸。
        // 用最矮的列估（無條件進位）才保證一次拿夠——寧可多留一點白，也不要留著捲軸。
        var decision = First(10, Sample(leftover: -70));

        Assert.Equal(8, decision.PageSize);
    }

    [Fact]
    public void Decide_OverflowingALot_ShouldShrinkInOneStep()
    {
        // 縮列用最矮的列估：寧可多縮一列留白，也不要留著使用者抱怨的那條捲軸。
        var decision = First(12, Sample(leftover: -200, tallest: TallRow, shortest: ShortRow));

        Assert.Equal(12 - 4, decision.PageSize);
    }

    [Fact]
    public void Decide_Overflowing_ShouldRecordTheCeiling()
    {
        // 記下「10 列會超出」，否則下一輪剩餘空間看起來又夠，就會再長回 10 列。
        var decision = First(10, Sample(leftover: -70));

        Assert.Equal(9, decision.State.Ceiling);
    }

    [Fact]
    public void Decide_AtCeiling_ShouldNotGrowBackToTheKnownBadSize()
    {
        // ⭐ 整組最重要的一條：這就是「來回跳」的防線。
        // 10 列已知會超出，所以就算剩餘空間看起來夠補好幾列，也不可以回到 10。
        var overflowed = First(10, Sample(leftover: -70));
        var atCeiling = TodoAutoFit.Decide(9, Sample(leftover: 200), overflowed.State);

        Assert.Equal(9, atCeiling.PageSize);
    }

    #endregion

    #region 收斂

    [Fact]
    public void Decide_ShouldSettle_WhenRowHeightsAreMixed()
    {
        // ⭐ 重播實跑的情境：補進來的那一列比估的高而撐出捲軸，縮回去之後剩餘空間又看起來夠補。
        // 沒有上限的話這裡會永遠跳下去，而且每跳一次都打一輪查詢。
        var state = TodoAutoFit.InitialState;
        var size = 6;
        var seen = new List<int>();

        for (var i = 0; i < 20; i++)
        {
            // 補上去就超出、縮回來就有空位——最惡劣的輸入。
            var leftover = size > 8 ? -20 : 70;
            var decision = TodoAutoFit.Decide(size, Sample(leftover, tallest: TallRow, shortest: ShortRow), state);
            size = decision.PageSize;
            state = decision.State;
            seen.Add(size);
        }

        // 最後至少五輪必須完全不動，才算真的停下來。
        Assert.Equal(1, seen.TakeLast(5).Distinct().Count());
    }

    [Fact]
    public void Decide_ShouldNotGoBelowMinimum()
    {
        // 排到剩一列的清單已經不算清單了，這種螢幕高度寧可讓畫面捲一點。
        var decision = First(4, Sample(leftover: -2000, tallest: TallRow, shortest: ShortRow));

        Assert.Equal(TodoAutoFit.MinimumRows, decision.PageSize);
    }

    [Fact]
    public void Decide_ShouldNotGoAboveMaximum()
    {
        // 上限擋的是離譜的量測值（例如列高量到 1px）變成一次撈幾百筆的查詢。
        var decision = First(30, Sample(leftover: 100000, tallest: 1, shortest: 1));

        Assert.Equal(TodoAutoFit.MaximumRows, decision.PageSize);
    }

    #endregion

    #region 版面換了

    [Fact]
    public void Decide_WidthChanged_ShouldDropTheCeiling()
    {
        // 折不折行取決於欄寬。側邊欄收合之後還抱著舊的上限，表格會白白少排好幾列。
        var overflowed = First(10, Sample(leftover: -70));
        var next = TodoAutoFit.Decide(
            overflowed.PageSize,
            Sample(leftover: 70, width: Width + 236),
            overflowed.State);

        Assert.True(next.PageSize > overflowed.PageSize, "換了版面寬度之後應該可以重新往上補。");
    }

    [Fact]
    public void Decide_ViewportGrew_ShouldDropTheCeiling()
    {
        // 畫面變高了，之前測出來的「放不下」當然不再成立。
        var overflowed = First(10, Sample(leftover: -70));
        var next = TodoAutoFit.Decide(
            overflowed.PageSize,
            Sample(leftover: 200, height: Height + 300),
            overflowed.State);

        Assert.True(next.PageSize > overflowed.PageSize, "畫面變高之後應該可以重新往上補。");
    }

    [Fact]
    public void Decide_ScrollbarWidthJitter_ShouldKeepTheCeiling()
    {
        // ⭐ 實跑抓到的：收斂過程中整頁捲軸會短暫出現，容器寬度跟著差一個捲軸寬。
        // 那不是「換了版面」，是我們自己造成的。當成換版面就會把上限清掉而回到已知會超出的列數。
        var overflowed = First(10, Sample(leftover: -70));
        var next = TodoAutoFit.Decide(9, Sample(leftover: 200, width: Width - 15), overflowed.State);

        Assert.Equal(9, next.PageSize);
    }

    #endregion

    #region 壞掉的量測值

    [Theory]
    [InlineData(double.NaN, ShortRow, ShortRow)]
    [InlineData(100, double.NaN, ShortRow)]
    [InlineData(100, ShortRow, double.NaN)]
    [InlineData(double.PositiveInfinity, ShortRow, ShortRow)]
    [InlineData(100, 0, 0)]          // ⚠️ 表格還沒有任何列時就是這個值
    [InlineData(100, -5, -5)]
    public void Decide_BadMeasurement_ShouldNotTouchThePageSize(double leftover, double tallest, double shortest)
    {
        // 壞值一動就是一輪查詢，而且可能一路把列數推到上限。什麼都不做才是對的。
        var decision = First(7, Sample(leftover, tallest, shortest));

        Assert.Equal(7, decision.PageSize);
    }

    [Fact]
    public void Constants_ShouldBeOrderedAndUsable()
    {
        Assert.True(TodoAutoFit.MinimumRows >= 1);
        Assert.InRange(TodoAutoFit.FallbackRows, TodoAutoFit.MinimumRows, TodoAutoFit.MaximumRows);
    }

    #endregion
}
