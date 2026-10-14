using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 釘住 <c>MainLayout.razor</c> 上幾個「少了也編譯得過、畫面也不會報錯」的全站掛載點。
///
/// <para>
/// 這不是假想的風險：0.4.84 改寫版面底部的註解時，把右下角的進度面板與 blazor-error-ui
/// 一起刪掉了。之後所有「可在右下角看到進度」的提示都落空，使用者在 1.0.25 回報才發現。
/// 元件本身、它的樣式與服務層的進度登錄全都還在，只差版面上那一行，沒有任何東西會失敗。
/// </para>
/// </summary>
public sealed partial class LayoutMountTests
{
    [Theory]
    [InlineData("<BackgroundJobProgressPanel", "右下角的上傳／轉錄／會議紀錄生成進度面板")]
    [InlineData("<FormModalHelper", "全站對話框外框樣式")]
    [InlineData("id=\"blazor-error-ui\"", "Blazor 未處理錯誤時的提示列")]
    public void MainLayout_ShouldMount(string marker, string purpose)
    {
        // 註解掉的不算——那正是「看起來還在、其實沒有渲染」的樣子。
        var markup = RazorCommentPattern().Replace(File.ReadAllText(FindMainLayout()), string.Empty);

        Assert.True(
            markup.Contains(marker, StringComparison.Ordinal),
            $"MainLayout.razor 少了 {marker}（{purpose}）。所有頁面都以這個版面為底，少了它整站都看不到。");
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorCommentPattern();

    private static string FindMainLayout()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MeetingRecord.Web", "Components", "Layout", "MainLayout.razor");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到 MeetingRecord.Web/Components/Layout/MainLayout.razor。");
    }
}
