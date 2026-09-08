using System.Text;

namespace MeetingRecord.Business.Services.AiChat;

/// <summary>問答脈絡的一份來源（一份會議紀錄、一份逐字稿或一個附件）。</summary>
/// <param name="Label">顯示名稱，會出現在脈絡標題與畫面的來源清單上。</param>
/// <param name="Content">全文。null 或空白代表這份來源取不到內容。</param>
public sealed record ChatSource(string Label, string? Content);

/// <summary>組好的脈絡，以及「哪些來源被截斷或跳過」。</summary>
/// <param name="Context">要送進 LLM 的脈絡全文。</param>
/// <param name="UsedLabels">實際被納入的來源。</param>
/// <param name="TruncatedLabels">因為超出預算而被截尾的來源。</param>
/// <param name="SkippedLabels">完全沒有納入的來源（取不到內容，或預算已用盡）。</param>
public sealed record ChatContextResult(
    string Context,
    IReadOnlyList<string> UsedLabels,
    IReadOnlyList<string> TruncatedLabels,
    IReadOnlyList<string> SkippedLabels)
{
    /// <summary>有沒有任何內容可以拿來回答。</summary>
    public bool HasContent => UsedLabels.Count > 0;
}

/// <summary>
/// 把多份來源組成一段脈絡，並在超出預算時**依序**截斷。
///
/// 抽成純函式以便單元測試——與 <c>TranscriptChunker</c>、
/// <c>TranscriptionNoiseFilter</c> 同一個慣例，不碰 IO、不碰資料庫。
/// </summary>
public static class ChatContextBuilder
{
    /// <summary>
    /// 脈絡的字元上限。
    ///
    /// <para>
    /// gpt-4o 系列的脈絡長度是 128k token，中文約 1～1.5 字元/token，
    /// 60000 字元約當 40～60k token，其餘留給系統提示詞、對話歷史與回答本身。
    /// </para>
    /// </summary>
    public const int DefaultMaxChars = 60000;

    /// <summary>被截斷的來源在結尾附上這行，讓 LLM 知道自己看到的不是全文。</summary>
    private const string TruncationNotice = "\n…（本段內容過長已截斷，後續內容未提供）";

    /// <summary>
    /// 依傳入順序組裝脈絡；**順序就是優先權**，呼叫端要把最該保留的放前面。
    ///
    /// <para>
    /// 一份來源剩餘空間不足時會被截尾並記進 <c>TruncatedLabels</c>；
    /// 空間已完全用盡時整份跳過並記進 <c>SkippedLabels</c>。
    /// 兩者都必須回報到畫面上——<b>絕不無聲截斷</b>，否則使用者會以為 AI 看過全部資料。
    /// </para>
    /// </summary>
    public static ChatContextResult Build(IEnumerable<ChatSource> sources, int maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        var builder = new StringBuilder();
        var used = new List<string>();
        var truncated = new List<string>();
        var skipped = new List<string>();
        var remaining = maxChars;

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Content))
            {
                skipped.Add(source.Label);
                continue;
            }

            // 分隔字串也要計進預算，否則來源一多就會超出上限。
            var separator = builder.Length > 0 ? "\n\n" : string.Empty;
            var header = $"### {source.Label}\n";
            var overhead = separator.Length + header.Length;
            var content = source.Content.Trim();

            // 放進去也只剩截斷提示、留不下實質內容時，整份跳過比較誠實。
            if (remaining <= overhead + TruncationNotice.Length)
            {
                skipped.Add(source.Label);
                continue;
            }

            var available = remaining - overhead;

            if (content.Length > available)
            {
                content = string.Concat(
                    content.AsSpan(0, available - TruncationNotice.Length),
                    TruncationNotice);

                truncated.Add(source.Label);
            }

            builder.Append(separator).Append(header).Append(content);
            used.Add(source.Label);
            remaining -= overhead + content.Length;
        }

        return new ChatContextResult(builder.ToString(), used, truncated, skipped);
    }
}
