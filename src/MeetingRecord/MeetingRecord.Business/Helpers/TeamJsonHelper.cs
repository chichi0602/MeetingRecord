using System.Text.Json;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 角色「預設團隊」以 JSON 字串陣列儲存於 RoleView.DefaultTeamsJson 的序列化輔助。
/// </summary>
public static class TeamJsonHelper
{
    /// <summary>序列化為 JSON（去頭尾空白、捨棄空白、忽略大小寫去重，保留原順序）。</summary>
    public static string Serialize(IEnumerable<string>? teams)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        foreach (var team in teams ?? [])
        {
            var trimmed = (team ?? string.Empty).Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                cleaned.Add(trimmed);
            }
        }

        return JsonSerializer.Serialize(cleaned);
    }

    /// <summary>從 JSON 還原團隊清單；無效或空白回傳空清單。</summary>
    public static List<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
