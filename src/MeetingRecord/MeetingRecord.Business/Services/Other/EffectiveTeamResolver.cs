using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;

namespace MeetingRecord.Business.Services.Other;

public sealed class EffectiveTeamResolver : IEffectiveTeamResolver
{
    private readonly BackendDBContext context;

    public EffectiveTeamResolver(BackendDBContext context)
    {
        this.context = context;
    }

    public async Task<IReadOnlyList<string>> GetEffectiveTeamNamesAsync(int userId)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        // 1) 直接綁在使用者的團隊（UserTeam）
        var userTeamNames = await context.UserTeam
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Join(context.Team, ut => ut.TeamId, t => t.Id, (ut, t) => t.Name)
            .ToListAsync();

        foreach (var name in userTeamNames)
        {
            AddDistinct(name, seen, result);
        }

        // 0.4.101 起角色不再帶預設團隊：有效團隊＝使用者直接所屬的團隊，沒有第二個來源。
        return result;
    }

    private static void AddDistinct(string? name, HashSet<string> seen, List<string> result)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > 0 && seen.Add(trimmed))
        {
            result.Add(trimmed);
        }
    }
}
