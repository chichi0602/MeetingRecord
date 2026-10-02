using System.Linq.Expressions;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 「分類」等多值標籤欄位的字串儲存輔助（0.4.103 起團隊不再用標籤，改走 Team／ProjectTeam）。
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
}
