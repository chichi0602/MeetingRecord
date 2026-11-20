namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 會議紀錄的舊版（1.1.71）：重新產生、手動編修、還原之前，把被蓋掉的內容留一份。
///
/// <para>
/// 寫入一律經 <c>MeetingDraftVersionRecorder</c>，與會議本身同一次存檔；每場最多保留
/// <see cref="MaxPerMeeting"/> 份，超過刪最舊的。會議刪除時一併刪除（明確 Cascade，全站預設 Restrict）；
/// 「從專案移除」會清掉會議紀錄，這時舊版也一起刪。
/// </para>
/// </summary>
public class MeetingDraftVersion
{
    public const int MaxPerMeeting = 20;

    public int Id { get; set; }

    public int MeetingId { get; set; }

    public Meeting? Meeting { get; set; }

    /// <summary>被蓋掉的會議紀錄內容（Markdown）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>那份內容當初用的提示詞名稱（快照）。</summary>
    public string? PromptTemplateName { get; set; }

    /// <summary>那份內容當初的與會人員（<c>TagStringHelper</c> 換行包夾字串，快照）。</summary>
    public string? Attendees { get; set; }

    /// <summary>為什麼留這一版：Regenerate、Edit、Restore（見 <c>MeetingDraftVersionReasons</c>）。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>那份內容原本產生或最後修改的時間。</summary>
    public DateTime? ContentAt { get; set; }

    public int? SavedByUserId { get; set; }

    /// <summary>造成這次覆蓋的人（快照）。</summary>
    public string? SavedByName { get; set; }

    /// <summary>留版的時間（本地時間）。</summary>
    public DateTime SavedAt { get; set; }
}
