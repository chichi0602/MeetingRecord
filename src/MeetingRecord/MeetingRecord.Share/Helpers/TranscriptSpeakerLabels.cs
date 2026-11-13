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
/// ⚠️ 代號是<b>每一個轉錄分段（約 5 分鐘）各自編</b>的：模型每次呼叫都從 A 重新開始，
/// 第 1 段的 A 和第 2 段的 A 不一定是同一人，也不是人名。
/// </para>
/// </summary>
public static partial class TranscriptSpeakerLabels
{
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
}
