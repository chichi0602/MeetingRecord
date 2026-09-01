using System.Text.RegularExpressions;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 提示詞內容的變數佔位符輔助。
///
/// 佔位符格式為 <c>{{變數名}}</c>（大括號內允許前後空白），目前支援的變數見
/// <see cref="KnownVariables"/>。未知變數只會回報供 UI 提醒，
/// <b>絕不阻擋儲存</b>——範本作者可能刻意先寫下尚未支援的佔位符。
/// </summary>
public static partial class PromptVariableHelper
{
    /// <summary>目前支援、產生會議紀錄時會被代入實際內容的變數名稱</summary>
    public static readonly IReadOnlyList<string> KnownVariables =
    [
        "transcript",
        "meetingTitle",
        "meetingDate"
    ];

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();

    /// <summary>
    /// 取出提示詞內容中不在支援清單內的變數名稱（忽略大小寫比對，去重並保留出現順序）。
    /// </summary>
    public static List<string> FindUnknownVariables(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var known = new HashSet<string>(KnownVariables, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();

        foreach (Match match in VariableRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            if (known.Contains(name))
            {
                continue;
            }

            if (seen.Add(name))
            {
                unknown.Add(name);
            }
        }

        return unknown;
    }

    /// <summary>
    /// 產生支援變數的說明文字，例如 "{{transcript}}、{{meetingTitle}}、{{meetingDate}}"。
    /// </summary>
    public static string DescribeKnownVariables()
    {
        return string.Join("、", KnownVariables.Select(x => $"{{{{{x}}}}}"));
    }

    /// <summary>
    /// 把提示詞內容中的佔位符代入實際值（產生會議紀錄時的代入端）。
    ///
    /// 比對不分大小寫，並允許大括號內有空白（與 <see cref="FindUnknownVariables"/> 同一個規則）。
    /// <b>找不到對應值的佔位符原樣保留</b>，與「未知變數只提醒、不阻擋」的立場一致：
    /// 悄悄代入空字串會讓範本作者看不出哪裡沒生效。
    /// </summary>
    public static string Render(string? content, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var lookup = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

        return VariableRegex().Replace(content, match =>
            lookup.TryGetValue(match.Groups[1].Value, out var replacement)
                ? replacement ?? string.Empty
                : match.Value);
    }
}
