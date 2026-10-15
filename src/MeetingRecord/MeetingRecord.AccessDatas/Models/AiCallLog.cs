using MeetingRecord.Share.Enums;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// AI 對話紀錄（1.0.29，移植自 NET10-Blazor-Starter 並改為 MeetingRecord 的功能別）。
/// **一次實際呼叫一列**，只存中繼資料；送出內容與回應全文存在 <c>AiCallLogPath</c> 的 JSON 檔
/// （<see cref="ContentFile"/>）。
///
/// <para>
/// 與用量帳本 <see cref="AiUsageLog"/> 以 <see cref="CallId"/> 一對一對應：同一次呼叫的
/// 「花了多少」看帳本，「說了什麼」看這裡。
/// </para>
///
/// <para>
/// 刻意**沒有外鍵**（同帳本）。但內容含會議逐字稿等敏感資料，所以刪除會議／專案時
/// 由服務層連帶刪除（<c>AiCallLogService.DeleteByMeetingAsync</c>／<c>DeleteByProjectAsync</c>），
/// 並有保留期限自動清除；帳本則不動。
/// </para>
/// </summary>
public class AiCallLog
{
    public int Id { get; set; }

    /// <summary>這次呼叫的識別碼，與 <see cref="AiUsageLog.CallId"/> 相同。</summary>
    public Guid CallId { get; set; }

    /// <summary>呼叫結束的時間（本地時間，與帳本一致）。</summary>
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    public AiUsageFeature Feature { get; set; }

    public AiUsageOutcome Outcome { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>觸發者；背景工作由佇列帶進來。</summary>
    public int? UserId { get; set; }

    /// <summary>觸發者姓名快照。</summary>
    public string? UserName { get; set; }

    public int? MeetingId { get; set; }

    public int? ProjectId { get; set; }

    /// <summary>給人看的關聯說明，例如「會議 #12〈週會〉分段摘要 2/5」。最多 500 字。</summary>
    public string? RelatedInfo { get; set; }

    /// <summary>
    /// 把同一趟工作的多次呼叫串起來（同一次會議紀錄生成、同一段 AI 問答、同一次轉錄）。
    /// 例如 <c>draft:12:{runId}</c>。最多 100 字。
    /// </summary>
    public string? ThreadKey { get; set; }

    /// <summary>供應商回的 HTTP 狀態碼；成功或沒拿到回應時為 null。</summary>
    public int? HttpStatus { get; set; }

    public long ElapsedMilliseconds { get; set; }

    /// <summary>送出內容的字元數（系統＋使用者訊息；轉錄為 0）。</summary>
    public int RequestCharacters { get; set; }

    /// <summary>收到的回應字元數（失敗或取消時為已收到的部分）。</summary>
    public int ResponseCharacters { get; set; }

    /// <summary>回應不完整：串流中途取消或失敗，內容檔裡只有已收到的部分。</summary>
    public bool IsResponsePartial { get; set; }

    /// <summary>失敗原因（截斷）。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>內容檔相對路徑 <c>yyyyMM/{CallId:N}.json</c>；寫檔失敗或停用記錄時為 null。</summary>
    public string? ContentFile { get; set; }
}
