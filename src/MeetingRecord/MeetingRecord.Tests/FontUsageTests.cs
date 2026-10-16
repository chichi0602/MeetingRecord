using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 全站同一套字型（1.0.41，使用者要求）。字型只能經 <c>--app-font-family</c>／<c>--app-font-mono</c> 兩個變數：
/// 元件裡寫死一份字型清單，只要漏了中文字型，中文就會被瀏覽器逐字挑字型，又變回大小粗細不一。
/// </summary>
public sealed partial class FontUsageTests
{
    [Fact]
    public void Stylesheets_ShouldOnlyUseFontVariables()
    {
        var web = FindWebProject();
        var stylesheets = Directory
            .EnumerateFiles(Path.Combine(web, "Components"), "*.css", SearchOption.AllDirectories)
            .Append(Path.Combine(web, "wwwroot", "app.css"));

        var offenders = stylesheets
            .SelectMany(path => FontFamilyPattern().Matches(File.ReadAllText(path))
                .Select(match => match.Groups["value"].Value.Trim())
                .Where(value => !value.StartsWith("var(--app-font-", StringComparison.Ordinal) && value != "inherit")
                .Select(value => $"{Path.GetFileName(path)}：{value}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"字型請用 var(--app-font-family) 或 var(--app-font-mono)，不要寫死：{string.Join("；", offenders)}");
    }

    [Fact]
    public void AppCss_ShouldDefineTraditionalChineseFont()
    {
        var css = File.ReadAllText(Path.Combine(FindWebProject(), "wwwroot", "app.css"));

        Assert.Matches(@"--app-font-family:[^;]*Microsoft JhengHei", css);
        Assert.Matches(@"--app-font-mono:[^;]*Microsoft JhengHei", css);
    }

    /// <summary>只抓 CSS 屬性 font-family，不抓變數定義（--app-font-family:）。</summary>
    [GeneratedRegex(@"(?<![-\w])font-family\s*:\s*(?<value>[^;}]+)")]
    private static partial Regex FontFamilyPattern();

    private static string FindWebProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MeetingRecord.Web");
            if (File.Exists(Path.Combine(candidate, "wwwroot", "app.css")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MeetingRecord.Web 專案目錄。");
    }
}
