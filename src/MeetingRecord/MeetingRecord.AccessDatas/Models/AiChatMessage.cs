namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// AI 問答的一則訊息。
///
/// <para>
/// <see cref="ProjectId"/> 與 <see cref="MeetingId"/> **恰有一個非 null**，用來決定
/// 這則訊息屬於哪一段對話：專案層級的問答（讀會議紀錄與附件）或單一會議的問答
/// （讀該會議的紀錄與逐字稿）。
/// </para>
///
/// <para>
/// 對話是**同一個專案／會議底下所有人共用**，不是每人一份——專案自 0.4.39 起
/// 已無列級權控，團隊成員看得到彼此問過什麼可以避免重複詢問。日後若要改成個人私有，
/// 加一個 MyUserId 過濾即可，不必改結構。
/// </para>
/// </summary>
public class AiChatMessage
{
    public int Id { get; set; }

    #region 對話歸屬（兩者恰有一個非 null）

    /// <summary>專案層級對話所屬的專案。刪除專案時一併刪除。</summary>
    public int? ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>
    /// 會議層級對話所屬的會議。刪除會議時一併刪除。
    ///
    /// 注意 <c>Meeting.ProjectId</c> 本身是 SetNull（刪專案不刪會議），
    /// 所以會議層級的對話不會因為專案被刪就消失——這是刻意的。
    /// </summary>
    public int? MeetingId { get; set; }

    public Meeting? Meeting { get; set; }

    #endregion

    /// <summary>訊息角色：<c>user</c> 或 <c>assistant</c>。</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 提問者的顯示名稱**快照**（assistant 訊息為 null）。
    ///
    /// 刻意不做 FK 到 MyUser：使用者改名、停用或刪除都不該讓歷史紀錄跟著變動或消失。
    /// </summary>
    public string? AskedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
