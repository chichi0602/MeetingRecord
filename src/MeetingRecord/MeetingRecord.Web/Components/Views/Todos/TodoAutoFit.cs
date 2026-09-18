namespace MeetingRecord.Web.Components.Views.Todos;

/// <summary>
/// 一次版面量測的結果（0.4.86）。全部由 <c>wwwroot/js/todo-auto-fit.js</c> 量出來。
/// </summary>
/// <param name="Leftover">
/// 表格區塊（含分頁器）的底部，離「畫面底部可用位置」還剩多少 px。**負數代表已經撐出捲軸**。
/// </param>
/// <param name="TallestRow">目前算繪出來最高的一列（px）。</param>
/// <param name="ShortestRow">目前算繪出來最矮的一列（px）。</param>
/// <param name="ContentWidth">表格容器寬度（px）。</param>
/// <param name="ViewportHeight">視窗高度（px）。</param>
public readonly record struct TodoFitSample(
    double Leftover,
    double TallestRow,
    double ShortestRow,
    double ContentWidth,
    double ViewportHeight);

/// <summary>
/// 控制器記住的東西：**這個版面已知放不下的列數下界**。
///
/// <para>
/// ⚠️ 沒有這個上限就會來回跳。列高不是固定的——待辦頁的截止日欄會把「逾期 N 天」
/// 折到第二行（0.4.67 刻意的排版），實測 63.75px 對 81px。所以「補一列」補進來的那一列
/// 可能比估的高而撐出捲軸，縮回去之後剩餘空間又看起來夠補一列……如此反覆，
/// 而且每跳一次都打一輪查詢。記下「N 列會超出」之後就不再長回 N，所以一定會停。
/// </para>
/// </summary>
/// <param name="ContentWidth">這個上限是在哪個表格寬度下測出來的。</param>
/// <param name="ViewportHeight">這個上限是在哪個視窗高度下測出來的。</param>
/// <param name="Ceiling">已知的可用列數上限。</param>
public readonly record struct TodoFitState(double ContentWidth, double ViewportHeight, int Ceiling)
{
    /// <summary>
    /// 版面寬度要變動超過這個值才算「換了版面」。
    ///
    /// <para>
    /// ⚠️ 不能用 0。收斂過程中整頁捲軸會短暫出現，表格容器寬度跟著差一個捲軸寬
    /// （Windows Chrome 約 15px），用 0 的話上限會被「自己造成的捲軸」清掉，
    /// 於是又開始來回跳。24px 大於任何常見捲軸寬，又遠小於側邊欄收合的 236px，
    /// 所以真的換版面時仍然會重來（欄寬變了，折不折行也會變）。
    /// </para>
    /// </summary>
    public const double WidthChangeThreshold = 24;

    /// <summary>這個上限還適用於 <paramref name="sample"/> 量到的版面嗎。</summary>
    public bool Matches(TodoFitSample sample)
        => Math.Abs(sample.ContentWidth - ContentWidth) <= WidthChangeThreshold
        && Math.Abs(sample.ViewportHeight - ViewportHeight) <= 0.5;
}

/// <summary>控制器的輸出：這一輪要用的每頁筆數，以及要帶到下一輪的狀態。</summary>
public readonly record struct TodoFitDecision(int PageSize, TodoFitState State);

/// <summary>
/// 待辦頁「列數剛好吃滿一個畫面」的決策（0.4.86）。
///
/// <para>
/// ⚠️ 這是**回授**不是預測。第一版是「可用高度 ÷ 記住的最高列高」，實跑量到表格底部
/// 永遠離畫面底 76～177px（而右邊面板永遠只差 51px）——因為記住的列高偏保守，
/// 再被無條件捨去吃掉一列。改成量「表格區塊底部離畫面底還剩多少」之後，
/// 剩餘空間夠就補、超出就縮，天生對列高不一致免疫。
/// </para>
///
/// <para>
/// 高度只有 DOM 量得到，所以量測在 <c>wwwroot/js/todo-auto-fit.js</c>；
/// 但決策留在這裡，理由與 <see cref="TodoOwnerFilter"/>／<c>TextSearchHelper</c> 一樣——
/// <b>本專案沒有 bUnit</b>，寫進 JS 或 <c>.razor.cs</c> 的判斷就只能靠眼睛驗。
/// </para>
/// </summary>
public static class TodoAutoFit
{
    /// <summary>
    /// 還沒量到任何東西時的每頁筆數（預先算繪、JS 還沒回報）。
    /// 取 6 是 1366×768 筆電大致放得下的列數——控制器會從這個值往上補或往下縮。
    /// </summary>
    public const int FallbackRows = 6;

    /// <summary>下限。再少就不像清單了，這種螢幕高度寧可讓畫面捲一點。</summary>
    public const int MinimumRows = 3;

    /// <summary>
    /// 上限。純粹擋住離譜的量測結果變成一次撈幾百筆的查詢。
    /// 4K 直向大約放得下三十幾列，所以 40 不會在真實螢幕上被踩到。
    /// </summary>
    public const int MaximumRows = 40;

    /// <summary>還沒量過任何版面時的起始狀態。</summary>
    public static TodoFitState InitialState => new(double.NaN, double.NaN, MaximumRows);

    /// <summary>
    /// 看一次量測結果，決定每頁要顯示幾列。
    ///
    /// <para>
    /// 收斂的理由：補列只在「還沒碰到上限」時才做，而每次撐出捲軸都會把上限往下壓一格；
    /// 列數又夾在 <see cref="MinimumRows"/> 與 <see cref="MaximumRows"/> 之間，所以一定會停。
    /// </para>
    /// </summary>
    public static TodoFitDecision Decide(int currentPageSize, TodoFitSample sample, TodoFitState state)
    {
        // 換了版面（寬度或視窗高度）就把上限丟掉：在舊版面測出來的「放不下」對新版面沒有意義，
        // 抱著它不放會讓表格在變高的畫面上白白少排好幾列。
        if (!state.Matches(sample))
        {
            state = new TodoFitState(sample.ContentWidth, sample.ViewportHeight, MaximumRows);
        }

        var current = Math.Clamp(currentPageSize, MinimumRows, MaximumRows);

        // 量不到列高就什麼都不要動——動一次就是一輪查詢。
        // 特別是 0：表格還沒有任何列時就是這個值，不擋的話會除出 Infinity。
        if (!IsUsable(sample.Leftover) || !IsUsable(sample.TallestRow) || !IsUsable(sample.ShortestRow)
            || sample.TallestRow <= 0 || sample.ShortestRow <= 0)
        {
            return new TodoFitDecision(current, state);
        }

        if (sample.Leftover < 0)
        {
            // 已經撐出捲軸了。用「最矮的一列」估要拿掉幾列——寧可多拿一列留白，也不要留著捲軸。
            var remove = Math.Max(1, (int)Math.Ceiling(-sample.Leftover / sample.ShortestRow));
            var shrunk = Math.Clamp(current - remove, MinimumRows, MaximumRows);

            // 這個版面已知放不下 current 列，記成上限，免得下一輪剩餘空間看起來又夠而長回去。
            var ceiling = Math.Clamp(Math.Min(state.Ceiling, current - 1), MinimumRows, MaximumRows);
            return new TodoFitDecision(shrunk, state with { Ceiling = ceiling });
        }

        // 剩下的空間連「最矮的一列」都放不下，就是已經吃滿了——那點留白是排不掉的。
        if (sample.Leftover < sample.ShortestRow || current >= state.Ceiling)
        {
            return new TodoFitDecision(current, state);
        }

        // 補列用「最高的一列」估，不用最矮的：估太樂觀會補到超出、再縮回來，
        // 使用者會看到捲軸閃一下。用最高的估最多少補一列，而下一輪還會再補。
        var add = Math.Max(1, (int)Math.Floor(sample.Leftover / sample.TallestRow));
        var grown = Math.Clamp(Math.Min(current + add, state.Ceiling), MinimumRows, MaximumRows);
        return new TodoFitDecision(grown, state);
    }

    private static bool IsUsable(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);
}
