using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 會議紀錄編修視窗的搜尋與取代（0.4.82）。
///
/// <para>
/// 這一版大部分是視覺改動，單元測試證明不了；但搜尋取代是純字串運算，
/// 而且它算出來的索引會直接餵給 JS 的 <c>setSelectionRange</c>——
/// 算錯的話反白會落在錯的地方、取代會改到錯的字，兩者都是**默默出錯**，
/// 不會有例外也不會有紅字。所以這裡測得比其他地方細。
/// </para>
/// </summary>
public sealed class TextSearchHelperTests
{
    #region FindAll

    [Fact]
    public void FindAll_ShouldNotOverlapMatches()
    {
        // 「aaaa」裡找「aa」是 2 筆（0、2）。
        // 推進寫成 at + 1 的話會得到 3 筆，畫面上的「共 m 筆」就永遠是錯的，
        // 而且第 3 筆與第 2 筆重疊，取代時會互相吃掉。
        var matches = TextSearchHelper.FindAll("aaaa", "aa");

        Assert.Equal(2, matches.Count);
        Assert.Equal(new TextMatch(0, 2), matches[0]);
        Assert.Equal(new TextMatch(2, 2), matches[1]);
    }

    [Fact]
    public void FindAll_ShouldIgnoreCase_AndKeepTermLength()
    {
        var matches = TextSearchHelper.FindAll("AI 助理與 ai 模型", "Ai");

        Assert.Equal(2, matches.Count);

        // 長度必須等於搜尋字串的長度。這一條釘住的是「比對方式不可以改成 culture-sensitive」——
        // 換成那種比對之後長度可能不相等，Start + Length 的算術就全錯了。
        Assert.All(matches, m => Assert.Equal(2, m.Length));
    }

    [Theory]
    [InlineData("會議紀錄", "")]
    [InlineData("會議紀錄", null)]
    [InlineData("", "會議")]
    [InlineData(null, "會議")]
    public void FindAll_ShouldReturnEmpty_WhenEitherSideIsBlank(string? text, string? term)
    {
        // 搜尋框被清空是常態操作。不擋的話 IndexOf 會一直回傳同一個位置 → 無限迴圈，
        // 整個 Blazor circuit 卡死（畫面停在那裡，連錯誤都看不到）。
        Assert.Empty(TextSearchHelper.FindAll(text, term));
    }

    [Fact]
    public void FindAll_ShouldCountSurrogatePairsAsTwoUnits()
    {
        // 𠮷 是擴充區漢字，在 UTF-16 裡是代理對，佔 2 個 code unit。
        // C# 的 string 索引與 JS 的 setSelectionRange 都以 code unit 計數，所以兩邊天然一致。
        // 這一條釘住的正是那個前提——有人把索引「改善」成 code point 的話這裡會紅。
        const string text = "會議𠮷紀錄";

        var matches = TextSearchHelper.FindAll(text, "紀錄");

        var match = Assert.Single(matches);
        Assert.Equal(4, match.Start);
        Assert.Equal("紀錄", text.Substring(match.Start, match.Length));
    }

    #endregion

    #region NormalizeNewLines

    [Fact]
    public void NormalizeNewLines_ShouldCollapseCrlfAndLoneCr()
    {
        Assert.Equal("a\nb\nc\nd", TextSearchHelper.NormalizeNewLines("a\r\nb\rc\nd"));
    }

    [Fact]
    public void NormalizeNewLines_ShouldKeepIndexesAlignedWithBrowser()
    {
        // 這是整個功能最容易默默出錯的地方，所以直接比對「正規化前後的索引差」。
        //
        // <textarea> 的 value 在 DOM 裡一律是 LF。原始文字含 \r\n 時，
        // C# 算出來的索引會比瀏覽器多算每一個 \r，反白從第一個換行之後就開始偏，
        // 而且愈往後偏愈多——只有長文件的後半段才看得出來。
        const string raw = "第一行\r\n第二行\r\n目標";

        var beforeNormalize = TextSearchHelper.FindAll(raw, "目標");
        var afterNormalize = TextSearchHelper.FindAll(TextSearchHelper.NormalizeNewLines(raw), "目標");

        Assert.Equal(10, Assert.Single(beforeNormalize).Start);

        // 兩個 \r 被拿掉，索引往前挪兩格——這才是瀏覽器看到的位置。
        Assert.Equal(8, Assert.Single(afterNormalize).Start);
    }

    #endregion

    #region ReplaceAll

    [Fact]
    public void ReplaceAll_ShouldNotCascade_WhenReplacementContainsTerm()
    {
        // 最典型的爆炸案例：把「a」換成「aa」。
        // 反覆取代的實作會愈換愈長甚至停不下來；一次掃描的話取代內容不會再被掃到。
        var (text, count) = TextSearchHelper.ReplaceAll("aa", "a", "aa");

        Assert.Equal("aaaa", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceAll_ShouldReplaceRealisticTermWithLongerOne()
    {
        var (text, count) = TextSearchHelper.ReplaceAll(
            "AI 產生的草稿由 AI 校對", "AI", "AI 助理");

        Assert.Equal("AI 助理 產生的草稿由 AI 助理 校對", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceAll_CountShouldMatchFindAll()
    {
        // 畫面上「共 m 筆」來自 FindAll、「已取代 N 筆」來自 ReplaceAll。
        // 兩者對不起來的話使用者會以為有幾筆沒換到。
        const string text = "會議紀錄、會議時間、會議室";

        var found = TextSearchHelper.FindAll(text, "會議");
        var (_, count) = TextSearchHelper.ReplaceAll(text, "會議", "研討");

        Assert.Equal(found.Count, count);
    }

    [Fact]
    public void ReplaceAll_ShouldAllowEmptyReplacement()
    {
        // 取代為空 = 刪除，是常用操作（清掉 AI 加的贅詞）。
        var (text, count) = TextSearchHelper.ReplaceAll("嗯我們嗯開始", "嗯", null);

        Assert.Equal("我們開始", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceAll_ShouldReturnSourceUnchanged_WhenNoMatch()
    {
        var (text, count) = TextSearchHelper.ReplaceAll("會議紀錄", "待辦", "工作");

        Assert.Equal("會議紀錄", text);
        Assert.Equal(0, count);
    }

    #endregion

    #region ReplaceAt

    [Fact]
    public void ReplaceAt_ShouldChangeOnlyTheChosenMatch()
    {
        const string text = "會議紀錄、會議時間、會議室";
        var matches = TextSearchHelper.FindAll(text, "會議");

        var (next, caret) = TextSearchHelper.ReplaceAt(text, matches, 1, "研討");

        // 只有第 2 筆變了，第 1 筆與第 3 筆原封不動。
        Assert.Equal("會議紀錄、研討時間、會議室", next);

        // 游標落在取代內容的結尾，接著按「下一個」才會往後走而不是原地打轉。
        Assert.Equal(matches[1].Start + 2, caret);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ReplaceAt_ShouldReturnSourceUnchanged_WhenIndexOutOfRange(int index)
    {
        // matches 會因為使用者繼續打字而過期。過期時原樣回傳比丟例外好——
        // 在 Blazor Server 上，元件事件裡的未處理例外會把整個 circuit 斷掉。
        const string text = "會議紀錄、會議時間、會議室";
        var matches = TextSearchHelper.FindAll(text, "會議");

        var (next, _) = TextSearchHelper.ReplaceAt(text, matches, index, "研討");

        Assert.Equal(text, next);
    }

    [Fact]
    public void ReplaceAt_ShouldReturnSourceUnchanged_WhenMatchIsStale()
    {
        // 索引在範圍內、但文字已經變短了：matches 是對舊文字算的。
        var staleMatches = TextSearchHelper.FindAll("會議紀錄、會議時間", "會議");

        var (next, _) = TextSearchHelper.ReplaceAt("會議", staleMatches, 1, "研討");

        Assert.Equal("會議", next);
    }

    #endregion

    #region ShiftCaret

    [Theory]
    // 取代字串較長：游標之前有 2 筆，每筆多 1 個字 → 往後挪 2。
    [InlineData(10, 2, 3, 12)]
    // 等長：不動。
    [InlineData(10, 2, 2, 10)]
    // 較短：游標之前有 2 筆，每筆少 1 個字 → 往前挪 2。
    [InlineData(10, 2, 1, 8)]
    public void ShiftCaret_ShouldFollowTheSameSemanticPosition(
        int caret, int termLength, int replacementLength, int expected)
    {
        // 「會議」出現在 0 與 4，兩筆都整個在游標（10）之前。
        var matches = TextSearchHelper.FindAll("會議紀錄會議時間內容在這裡", "會議");
        Assert.Equal(2, matches.Count);

        var shifted = TextSearchHelper.ShiftCaret(matches, caret, termLength, replacementLength);

        Assert.Equal(expected, shifted);
    }

    [Fact]
    public void ShiftCaret_ShouldIgnoreMatchesAfterTheCaret()
    {
        // 游標在第 1 筆之後、第 2 筆之前，所以只有第 1 筆算數。
        var matches = TextSearchHelper.FindAll("會議紀錄會議時間", "會議");

        Assert.Equal(4, TextSearchHelper.ShiftCaret(matches, 3, 2, 3));
    }

    #endregion

    #region NextIndex / PreviousIndex

    [Theory]
    [InlineData(-1, 3, 0)]   // 還沒選過任何一筆 → 從第一筆開始
    [InlineData(0, 3, 1)]
    [InlineData(2, 3, 0)]    // 到底迴繞
    [InlineData(0, 0, -1)]   // 沒有任何一筆
    public void NextIndex_ShouldWrapAround(int current, int count, int expected)
        => Assert.Equal(expected, TextSearchHelper.NextIndex(current, count));

    [Theory]
    [InlineData(-1, 3, 2)]   // 還沒選過 → 從最後一筆往回
    [InlineData(0, 3, 2)]    // 到頂迴繞
    [InlineData(2, 3, 1)]
    [InlineData(0, 0, -1)]
    public void PreviousIndex_ShouldWrapAround(int current, int count, int expected)
        => Assert.Equal(expected, TextSearchHelper.PreviousIndex(current, count));

    #endregion
}
