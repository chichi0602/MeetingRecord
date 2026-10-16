using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 釘住 1.0.38 的決定：提示一律用 <c>HoverTip</c>（瀏覽器端顯示），不用 AntDesign 的 Tooltip 元件。
///
/// <para>
/// AntDesign Tooltip 的顯示／隱藏要經 Blazor 連線來回，連到遠端主機時滑鼠快速掃過，
/// 「離開」事件會漏掉，提示框一個個卡在畫面上（使用者在正式機回報）。
/// 新頁面照舊範本複製一份工具列就會把它帶回來，編譯與畫面都不會報錯，所以用測試擋。
/// </para>
/// </summary>
public sealed partial class HoverTipUsageTests
{
    [Fact]
    public void Components_ShouldNotUseAntDesignTooltip()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(FindWebProject(), "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(path => AntTooltipPattern().IsMatch(RazorCommentPattern().Replace(File.ReadAllText(path), string.Empty)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"以下檔案用了 AntDesign 的 Tooltip，請改用 <HoverTip>（Blazor Server 下提示框會卡在畫面上）：{string.Join("、", offenders)}");
    }

    [Fact]
    public void SortableColumns_ShouldDisableSorterTooltip()
    {
        // 可排序欄位的表頭內建「按一下依遞增排序」提示，用的也是 AntDesign Tooltip，同樣會卡住（1.0.38 使用者回報）。
        // 1.6.0 只有欄位層級的開關，所以每個 Sortable 都要配一個 ShowSorterTooltip="false"。
        var offenders = Directory
            .EnumerateFiles(Path.Combine(FindWebProject(), "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(path => (Name: Path.GetFileName(path), Markup: RazorCommentPattern().Replace(File.ReadAllText(path), string.Empty)))
            .Where(file => BareSortablePattern().Matches(file.Markup).Count
                           != file.Markup.Split("ShowSorterTooltip=\"false\"").Length - 1)
            .Select(file => file.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"以下檔案有可排序欄位沒加 ShowSorterTooltip=\"false\"（表頭排序提示會卡在畫面上）：{string.Join("、", offenders)}");
    }

    [Fact]
    public void App_ShouldLoadHoverTipScript()
    {
        // 少了這支，HoverTip 只剩 data-hover-tip 屬性，整站的提示都不會出現，也不會有任何錯誤。
        var markup = RazorCommentPattern().Replace(
            File.ReadAllText(Path.Combine(FindWebProject(), "Components", "App.razor")),
            string.Empty);

        Assert.Contains("js/hover-tip.js", markup, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<(AntDesign\.)?Tooltip[\s>]")]
    private static partial Regex AntTooltipPattern();

    /// <summary>沒有帶值的 Sortable（帶值的 Sortable="false" 本來就沒有排序提示）。</summary>
    [GeneratedRegex(@"\bSortable(?=[\s/>])")]
    private static partial Regex BareSortablePattern();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorCommentPattern();

    private static string FindWebProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MeetingRecord.Web");
            if (File.Exists(Path.Combine(candidate, "Components", "App.razor")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MeetingRecord.Web 專案目錄。");
    }
}
