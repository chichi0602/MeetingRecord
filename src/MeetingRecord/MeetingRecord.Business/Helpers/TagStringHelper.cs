using System.Linq.Expressions;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 「分類 / 團隊」等多值標籤欄位的字串儲存輔助。
///
/// 儲存格式：以分隔字元（換行）包夾每個值，例如 "\n分類A\n分類B\n"。
/// 這樣可用 Field.Contains("\n分類A\n") 在 SQLite 上做「精確成員」比對，
/// 避免子字串誤判（例如「團隊」誤中「團隊2」）。
/// </summary>
public static class TagStringHelper
{
    public const string Delimiter = "\n";

    /// <summary>
    /// 將標籤清單轉為儲存字串。會去除頭尾空白、捨棄空白項、忽略大小寫去重（保留原順序）。
    /// 無任何有效值時回傳 null。
    /// </summary>
    public static string? ToStored(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        foreach (var value in values)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                cleaned.Add(trimmed);
            }
        }

        if (cleaned.Count == 0)
        {
            return null;
        }

        return Delimiter + string.Join(Delimiter, cleaned) + Delimiter;
    }

    /// <summary>
    /// 將儲存字串還原為標籤清單。
    /// </summary>
    public static List<string> ToList(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        return stored
            .Split(Delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// 將單一名稱包成可供 Contains 精確比對的片段："\n名稱\n"。
    /// </summary>
    public static string Wrap(string name)
    {
        return Delimiter + (name ?? string.Empty).Trim() + Delimiter;
    }

    /// <summary>
    /// 建立「欄位包含任一指定值」的查詢述詞（OR 串接），供 EF Core 轉為 SQL LIKE。
    /// 例如：分類過濾選了 [A,B] → x.Categories 含 \nA\n 或 含 \nB\n。
    /// 空清單回傳「永遠成立」。
    /// </summary>
    public static Expression<Func<T, bool>> BuildContainsAnyPredicate<T>(
        Expression<Func<T, string?>> fieldSelector,
        IReadOnlyCollection<string> values)
    {
        if (values is null || values.Count == 0)
        {
            return _ => true;
        }

        var parameter = fieldSelector.Parameters[0];
        var field = fieldSelector.Body;
        var containsMethod = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var nullConstant = Expression.Constant(null, typeof(string));

        Expression? body = null;
        foreach (var value in values)
        {
            var wrapped = Expression.Constant(Wrap(value), typeof(string));
            var notNull = Expression.NotEqual(field, nullConstant);
            var contains = Expression.Call(field, containsMethod, wrapped);
            var clause = Expression.AndAlso(notNull, contains);
            body = body is null ? clause : Expression.OrElse(body, clause);
        }

        return Expression.Lambda<Func<T, bool>>(body!, parameter);
    }

    /// <summary>
    /// 建立「團隊可見性」查詢述詞：紀錄無團隊（公開）或團隊與授權團隊有交集即可見。
    /// 供非管理員使用者過濾紀錄；管理員不應呼叫此述詞（直接看全部）。
    /// </summary>
    public static Expression<Func<T, bool>> BuildTeamAccessPredicate<T>(
        Expression<Func<T, string?>> teamSelector,
        IReadOnlyCollection<string> allowedTeams)
    {
        var parameter = teamSelector.Parameters[0];
        var field = teamSelector.Body;
        var nullConstant = Expression.Constant(null, typeof(string));
        var emptyConstant = Expression.Constant(string.Empty, typeof(string));

        // 公開：Teams == null || Teams == ""
        Expression body = Expression.OrElse(
            Expression.Equal(field, nullConstant),
            Expression.Equal(field, emptyConstant));

        if (allowedTeams is { Count: > 0 })
        {
            var containsMethod = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
            foreach (var team in allowedTeams)
            {
                var wrapped = Expression.Constant(Wrap(team), typeof(string));
                var notNull = Expression.NotEqual(field, nullConstant);
                var contains = Expression.Call(field, containsMethod, wrapped);
                body = Expression.OrElse(body, Expression.AndAlso(notNull, contains));
            }
        }

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>
    /// 單筆紀錄的團隊可見性判斷（記憶體端）：管理員一律可見；否則無團隊或與授權團隊有交集才可見。
    /// </summary>
    public static bool IsTeamAccessible(string? stored, IReadOnlyCollection<string> allowedTeams, bool isAdmin)
    {
        if (isAdmin)
        {
            return true;
        }

        var recordTeams = ToList(stored);
        if (recordTeams.Count == 0)
        {
            return true; // 無團隊視為公開
        }

        if (allowedTeams is not { Count: > 0 })
        {
            return false;
        }

        return recordTeams.Any(rt => allowedTeams.Any(at => string.Equals(rt, at.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
}
