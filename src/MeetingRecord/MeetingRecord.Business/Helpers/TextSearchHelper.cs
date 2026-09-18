using System.Text;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 文字中的一次出現。<see cref="Start"/> 與 <see cref="Length"/> 的單位是
/// UTF-16 code unit，與 JavaScript 字串的索引單位相同。
/// </summary>
public readonly record struct TextMatch(int Start, int Length);

/// <summary>
/// 編輯器的搜尋與取代。
///
/// <para>
/// 抽成純靜態函式是為了**測得到**：本專案沒有 bUnit，寫進 <c>.razor.cs</c> 或 JS 的邏輯
/// 就只能靠眼睛驗。這裡每一支都是 string 進、string 出，沒有任何 DOM 或狀態。
/// </para>
///
/// <para>
/// ⚠️ 比對一律用 <see cref="StringComparison.OrdinalIgnoreCase"/>，**不可以**換成
/// CurrentCulture／InvariantCulture 的版本。Ordinal 保證「比對到的片段長度 == 搜尋字串長度」，
/// 後面 <c>Start + Length</c> 的算術才成立；culture-sensitive 的比對會做多字元折疊
/// （德文 ß↔SS 之類），長度不相等，反白範圍與取代結果都會歪掉。
/// </para>
///
/// <para>
/// ⚠️ 這裡算出來的索引會直接餵給 JS 的 <c>setSelectionRange</c>。兩邊剛好都以
/// UTF-16 code unit 計數，所以 emoji 與擴充區漢字（代理對，各佔 2 個 code unit）
/// 天然一致，**不要**轉成 code point。但前提是文字已經用
/// <see cref="NormalizeNewLines"/> 正規化成 LF——見該方法的說明。
/// </para>
/// </summary>
public static class TextSearchHelper
{
    /// <summary>
    /// 把 CRLF 與單獨的 CR 正規化成 LF。
    ///
    /// <para>
    /// ⚠️ 這一步不可省。<c>&lt;textarea&gt;</c> 的 <c>value</c> 在 DOM 裡一律是 LF，
    /// 但會議紀錄來自 LLM 與資料庫，可能含 <c>\r\n</c>。不正規化的話，C# 這邊算出來的
    /// 索引從**第一個換行之後**就比瀏覽器多一格，而且每多一行就多偏一格——
    /// 反白會愈往後愈歪。這種錯只有在長文件的後半段才看得出來，所以要在入口就擋掉。
    /// </para>
    /// </summary>
    public static string NormalizeNewLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n');
    }

    /// <summary>
    /// 找出所有**不重疊**的出現位置，由前往後。
    ///
    /// <para>
    /// 搜尋字串為空時回空集合——搜尋框被清空是常態，不是錯誤；
    /// 而且不擋的話 <c>IndexOf</c> 會一直回傳同一個位置，變成無限迴圈。
    /// </para>
    /// </summary>
    public static IReadOnlyList<TextMatch> FindAll(string? text, string? term)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
        {
            return [];
        }

        var result = new List<TextMatch>();
        var from = 0;

        while (from <= text.Length - term.Length)
        {
            var at = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                break;
            }

            result.Add(new TextMatch(at, term.Length));

            // 從**這一筆的結尾**繼續找，不是 at + 1：
            // 「aaaa」裡找「aa」應該是 2 筆（0、2），不是 3 筆。
            from = at + term.Length;
        }

        return result;
    }

    /// <summary>
    /// 取代指定的那一筆，其餘完全不動。
    /// 回傳新文字，以及取代後游標應該落在的位置（取代內容的結尾）。
    ///
    /// <para>索引越界時原樣回傳，不丟例外——呼叫端的 matches 可能已經因為打字而過期。</para>
    /// </summary>
    public static (string Text, int Caret) ReplaceAt(
        string? text,
        IReadOnlyList<TextMatch> matches,
        int index,
        string? replacement)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var source = text ?? string.Empty;
        if (index < 0 || index >= matches.Count)
        {
            return (source, 0);
        }

        var match = matches[index];
        if (match.Start < 0 || match.Start + match.Length > source.Length)
        {
            return (source, 0);
        }

        var value = replacement ?? string.Empty;
        var next = string.Concat(
            source.AsSpan(0, match.Start),
            value,
            source.AsSpan(match.Start + match.Length));

        return (next, match.Start + value.Length);
    }

    /// <summary>
    /// 全部取代。回傳新文字與實際取代的筆數。
    ///
    /// <para>
    /// ⚠️ 刻意用 <see cref="FindAll"/> 的結果一次掃描組出來，而不是反覆呼叫 Replace。
    /// 取代字串**包含**搜尋字串時（把「AI」換成「AI 助理」），反覆取代會愈換愈長、
    /// 甚至停不下來。用一次掃描的話取代內容不會再被掃到，天然安全。
    /// </para>
    ///
    /// <para>
    /// 另一個好處是筆數與 <see cref="FindAll"/> **由建構保證一致**：
    /// 畫面上顯示「共 m 筆」與「已取代 N 筆」的來源是同一個，不會對不起來。
    /// </para>
    /// </summary>
    public static (string Text, int Count) ReplaceAll(string? text, string? term, string? replacement)
    {
        var source = text ?? string.Empty;
        var matches = FindAll(source, term);
        if (matches.Count == 0)
        {
            return (source, 0);
        }

        var value = replacement ?? string.Empty;
        var builder = new StringBuilder(source.Length);
        var copied = 0;

        foreach (var match in matches)
        {
            builder.Append(source, copied, match.Start - copied);
            builder.Append(value);
            copied = match.Start + match.Length;
        }

        builder.Append(source, copied, source.Length - copied);

        return (builder.ToString(), matches.Count);
    }

    /// <summary>
    /// 全部取代之後，原本在 <paramref name="caret"/> 的游標應該移到哪裡。
    ///
    /// <para>
    /// 用途是「全部取代不要讓畫面亂跳」：把游標丟到最後一筆被取代處聽起來貼心，
    /// 實際上會把使用者捲到文件末尾、失去位置感。位移量就是
    /// 「游標之前被取代的筆數 × 長度差」。
    /// </para>
    /// </summary>
    public static int ShiftCaret(
        IReadOnlyList<TextMatch> matches,
        int caret,
        int termLength,
        int replacementLength)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (caret <= 0)
        {
            return 0;
        }

        var delta = replacementLength - termLength;
        if (delta == 0)
        {
            return caret;
        }

        // 只算「整筆都在游標之前」的。跨在游標上的那一筆位移多少沒有正確答案，
        // 算成 0 至少不會把游標推到被取代的內容中間。
        var before = 0;
        foreach (var match in matches)
        {
            if (match.Start + match.Length <= caret)
            {
                before++;
            }
            else
            {
                break;
            }
        }

        return Math.Max(0, caret + (before * delta));
    }

    /// <summary>下一筆，到底之後迴繞回第一筆。0 筆時回 -1。</summary>
    public static int NextIndex(int current, int count)
        => count <= 0 ? -1 : (current < 0 ? 0 : (current + 1) % count);

    /// <summary>上一筆，到頂之後迴繞到最後一筆。0 筆時回 -1。</summary>
    public static int PreviousIndex(int current, int count)
        => count <= 0 ? -1 : (current <= 0 ? count - 1 : current - 1);
}
