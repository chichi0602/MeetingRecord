namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 解析使用者的有效團隊名稱。
/// 0.4.101 起有效團隊＝使用者直接所屬的 UserTeam；角色不再帶預設團隊（DefaultTeamsJson 已刪除）。
/// </summary>
public interface IEffectiveTeamResolver
{
    Task<IReadOnlyList<string>> GetEffectiveTeamNamesAsync(int userId);
}
