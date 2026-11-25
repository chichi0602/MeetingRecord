using System.Text.RegularExpressions;

namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 逐字稿行首的講者代號（「講者 A：」）。1.1.63 轉錄改用 gpt-4o-transcribe-diarize 後才有。
///
/// <para>
/// 放在 Share 是因為三個地方要用同一個判斷：轉錄時產生代號（Business）、截斷估算前去掉代號（Business）、
/// 逐字稿頁籤顯示說明（Web.Client 只能相依 Share）。
/// </para>
///
/// <para>
/// ⚠️ 字母代號是<b>每一個轉錄分段（約 5 分鐘）各自編</b>的：模型每次呼叫都從 A 重新開始，
/// 第 1 段的 A 和第 2 段的 A 不一定是同一人，也不是人名。
/// 1.1.64 起有聲音參考的講者改用<b>數字編號（講者 1～4）</b>，整場是同一人，可以在「標示講者」對到真名。
/// </para>
/// </summary>
public static partial class TranscriptSpeakerLabels
{
    /// <summary>「標示講者」的名字長度上限。</summary>
    public const int MaxNameLength = 20;

    /// <summary>模型沒給講者時用的代號。</summary>
    public const string UnknownSpeaker = "未知";

    /// <summary>組出行首代號，例如 <c>講者 A：</c>。</summary>
    public static string Format(string speaker) => $"講者 {speaker}：";

    /// <summary>行首的代號。代號限 12 字以內，避免把一般句子裡的「講者 …：」誤當代號。</summary>
    [GeneratedRegex(@"^講者 [^：\r\n]{1,12}：", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    /// <summary>逐字稿裡有沒有講者代號（舊的逐字稿沒有）。</summary>
    public static bool HasLabels(string? text) =>
        !string.IsNullOrEmpty(text) && LabelPattern().IsMatch(text);

    /// <summary>去掉每一行行首的講者代號，只留講話內容。</summary>
    public static string Strip(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : LabelPattern().Replace(text, string.Empty);

    /// <summary>
    /// 1.1.64：行首的數字編號（整場對齊的講者），第 1 組是編號，第 2 組是這行的內容。
    /// 1.1.73 起也認「2-1」：LLM Speech 超過單檔上限切段送出時，第 2 段的講者 1（各段分開編號，可以分開命名）。
    /// </summary>
    [GeneratedRegex(@"^講者 (\d{1,2}(?:-\d{1,2})?)：(.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AlignedLinePattern();

    /// <summary>只在該段有效的代號（字母、未知），也就是不是數字編號的那些。</summary>
    [GeneratedRegex(@"^講者 (?!\d{1,2}(?:-\d{1,2})?：)[^：\r\n]{1,12}：", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SegmentLabelPattern();

    /// <summary>逐字稿裡有沒有只在該段有效的代號（1.1.63 的舊逐字稿全部都是；第 5 位以後的講者也是）。</summary>
    public static bool HasSegmentLabels(string? text) =>
        !string.IsNullOrEmpty(text) && SegmentLabelPattern().IsMatch(text);

    /// <summary>逐字稿裡整場對齊的講者編號，依數字排序。</summary>
    public static IReadOnlyList<string> ListAlignedSpeakers(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return AlignedLinePattern().Matches(text)
            .Select(x => x.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => SortKey(x).Part)
            .ThenBy(x => SortKey(x).Speaker)
            .ToList();
    }

    /// <summary>「3」排成 (0, 3)、「2-1」排成 (2, 1)：沒切段的在前，切段的依段落再依講者。</summary>
    private static (int Part, int Speaker) SortKey(string label)
    {
        var dash = label.IndexOf('-');
        return dash < 0
            ? (0, int.Parse(label, System.Globalization.CultureInfo.InvariantCulture))
            : (int.Parse(label[..dash], System.Globalization.CultureInfo.InvariantCulture),
               int.Parse(label[(dash + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 某位講者說過的話，幫使用者辨認是誰：挑最長的幾句（照出現順序），每句超過上限就截斷。
    /// </summary>
    public static IReadOnlyList<string> SampleLines(string? text, string speaker, int count = 3, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return AlignedLinePattern().Matches(text)
            .Where(x => x.Groups[1].Value == speaker)
            .Select((x, index) => (Index: index, Line: x.Groups[2].Value.Trim()))
            .Where(x => x.Line.Length > 0)
            .OrderByDescending(x => x.Line.Length)
            .Take(count)
            .OrderBy(x => x.Index)
            .Select(x => x.Line.Length > maxLength ? x.Line[..maxLength] + "…" : x.Line)
            .ToList();
    }

    /// <summary>
    /// 把行首的「講者 1：」換成「王小明：」。沒有對照的編號、字母代號都維持原樣。
    /// 逐字稿檔案本身不改，讀取時才套用（見 <c>Meeting.SpeakerNames</c>）。
    /// </summary>
    public static string ApplyNames(string? text, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrEmpty(text) || names is not { Count: > 0 })
        {
            return text ?? string.Empty;
        }

        return AlignedLinePattern().Replace(text, match =>
            names.TryGetValue(match.Groups[1].Value, out var name) && !string.IsNullOrWhiteSpace(name)
                ? $"{name}：{match.Groups[2].Value}"
                : match.Value);
    }

    /// <summary>讀出 <c>Meeting.SpeakerNames</c> 的 JSON；空白或壞掉的內容當成沒有對照。</summary>
    public static Dictionary<string, string> ParseNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>寫回 <c>Meeting.SpeakerNames</c>；沒有任何對照時存 null。</summary>
    public static string? SerializeNames(IReadOnlyDictionary<string, string> names) =>
        names.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(names);

    /// <summary>名字能不能用：去掉前後空白後不可超過上限，也不能有換行或全形冒號（會弄壞行首代號）。</summary>
    public static bool IsValidName(string name) =>
        name.Length <= MaxNameLength && name.IndexOfAny(['\r', '\n', '：']) < 0;
}
