using System.Text;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>組好的使用者訊息與送出範圍。</summary>
public sealed class AiPromptBuildResult
{
    public string UserMessage { get; init; } = string.Empty;

    /// <summary>查詢結果的總筆數。</summary>
    public int TotalEntryCount { get; init; }

    /// <summary>實際送出的筆數（最新的那幾筆）。</summary>
    public int IncludedEntryCount { get; init; }

    /// <summary>是否因筆數上限而只送了一部分。</summary>
    public bool DroppedByEntryLimit { get; init; }

    public int CharacterCount { get; init; }

    public bool IsEmpty => IncludedEntryCount == 0;
}

/// <summary>
/// 把查詢結果組成要送給模型的使用者訊息。純函式，無 I/O、無 DI。
/// 0.4.121 移植自 NET10-Blazor-Starter。
/// </summary>
public static class AiLogPromptBuilder
{
    /// <summary>一次最多送幾筆（使用者 0.4.121 決定比照 Starter 的 100 筆，不做成設定）。</summary>
    public const int MaxEntries = 100;

    /// <summary>每筆日誌之間的分隔行。</summary>
    public const string EntrySeparator = "---";

    /// <summary>
    /// 組出使用者訊息。只有一道處理：從時間正序的來源取最新
    /// <paramref name="maxEntries"/> 筆，內容<b>原封不動</b>送出。
    ///
    /// <para>
    /// <b>完全不做字元層級的截斷</b>：截斷會把真正有用的堆疊切掉尾巴，而堆疊尾巴常常才是根因所在。
    /// 代價是提示詞大小只剩筆數這一道界線；真的撞到模型的內容視窗上限時，畫面會請使用者縮小查詢範圍。
    /// </para>
    /// </summary>
    public static AiPromptBuildResult Build(IReadOnlyList<LogEntry>? entriesAscending, int maxEntries = MaxEntries)
    {
        var total = entriesAscending?.Count ?? 0;
        if (entriesAscending is null || total == 0)
        {
            return new AiPromptBuildResult();
        }

        // 呼叫端可能傳 0 或負數。這裡夾住而不是丟例外，避免整個功能因手誤全掛。
        var entryLimit = Math.Max(1, maxEntries);

        var skip = Math.Max(0, total - entryLimit);
        var droppedByEntryLimit = skip > 0;

        // 統一換行為 LF，讓字元數與模型實際看到的內容一致。內容本身完整保留。
        var blocks = entriesAscending
            .Skip(skip)
            .Select(entry => (entry.Raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n'))
            .ToList();

        // ⚠️ 一律用 '\n' 而不是 AppendLine：AppendLine 會用 Environment.NewLine，在 Windows 上
        // 是 CRLF，會讓訊息混用兩種行尾。
        var builder = new StringBuilder();
        builder.Append("以下是 ").Append(blocks.Count).Append(" 筆應用程式日誌（時間正序，最舊在前），每筆以 ")
            .Append(EntrySeparator).Append(" 分隔。\n");
        if (droppedByEntryLimit)
        {
            builder.Append("（原始查詢共 ").Append(total)
                .Append(" 筆，受筆數上限僅送出最新 ").Append(blocks.Count).Append(" 筆。）\n");
        }

        foreach (var block in blocks)
        {
            builder.Append(EntrySeparator).Append('\n').Append(block).Append('\n');
        }

        var message = builder.ToString();
        return new AiPromptBuildResult
        {
            UserMessage = message,
            TotalEntryCount = total,
            IncludedEntryCount = blocks.Count,
            DroppedByEntryLimit = droppedByEntryLimit,
            CharacterCount = message.Length,
        };
    }
}
