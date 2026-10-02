namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 專案與團隊的關聯（0.4.102）。團隊＝「誰的資料」，角色＝「能做什麼」，分類＝「什麼資料」，三者分開。
///
/// <para>
/// 每個專案恰好一筆 <see cref="IsPrimary"/>（主責團隊，必填），另有 0～多筆協作團隊。
/// 使用者所屬的團隊（<see cref="UserTeam"/>）與主責＋協作有交集才看得到；沒有「公開」專案。
/// 判斷一律經 <c>ProjectAccessService</c>，「恰好一筆主責」由服務層保證。
/// </para>
/// </summary>
public class ProjectTeam
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public Project? Project { get; set; }

    public int TeamId { get; set; }

    public Team? Team { get; set; }

    /// <summary>主責團隊（true）或協作團隊（false）。</summary>
    public bool IsPrimary { get; set; }
}
