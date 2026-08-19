namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 解析使用者的有效團隊清單（用於列級權控）。
/// 有效團隊 = 直接綁在使用者的 UserTeam ∪ 其角色的 DefaultTeamsJson（聯集、去重）。
/// 以聯集方式相容「團隊綁角色」與「團隊綁使用者」兩種來源。
/// </summary>
public interface IEffectiveTeamResolver
{
    Task<IReadOnlyList<string>> GetEffectiveTeamNamesAsync(int userId);
}
