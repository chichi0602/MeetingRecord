using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 釘住版面上幾個「少了也編譯得過、畫面也不會報錯」的全站掛載點。
///
/// <para>
/// 這不是假想的風險：0.4.84 改寫版面底部的註解時，把右下角的進度面板與 blazor-error-ui
/// 一起刪掉了。之後所有「可在右下角看到進度」的提示都落空，使用者在 1.0.25 回報才發現。
/// 元件本身、它的樣式與服務層的進度登錄全都還在，只差版面上那一行，沒有任何東西會失敗。
/// </para>
///
/// <para>
/// 1.1.5（WASM 遷移）起外觀抽成共用的 <c>AppShell</c>：Server 頁的 <c>MainLayout</c> 與 WASM 頁的 <c>WasmLayout</c> 都用它。
/// 全站樣式與錯誤提示列掛在 AppShell；通知鈴與進度面板是 Server 頁才有的元件，由 MainLayout 經插槽傳進去。
/// </para>
/// </summary>
public sealed partial class LayoutMountTests
{
    [Theory]
    [InlineData("MeetingRecord.Web/Components/Layout/MainLayout.razor", "<AppShell", "共用外框")]
    [InlineData("MeetingRecord.Web/Components/Layout/MainLayout.razor", "<BackgroundJobProgressPanel", "右下角的上傳／轉錄／會議紀錄生成進度面板")]
    [InlineData("MeetingRecord.Web/Components/Layout/MainLayout.razor", "<NotificationBell", "右上角的站內通知鈴鐺（1.0.44）")]
    [InlineData("MeetingRecord.Web.Client/Components/Layout/AppShell.razor", "<FormModalHelper", "全站對話框外框樣式")]
    [InlineData("MeetingRecord.Web.Client/Components/Layout/AppShell.razor", "id=\"blazor-error-ui\"", "Blazor 未處理錯誤時的提示列")]
    [InlineData("MeetingRecord.Web.Client/Components/Layout/AppShell.razor", "@NotificationContent", "通知鈴插槽")]
    [InlineData("MeetingRecord.Web.Client/Components/Layout/AppShell.razor", "@OverlayContent", "進度面板插槽")]
    [InlineData("MeetingRecord.Web.Client/Layout/WasmLayout.razor", "<AppShell", "共用外框")]
    [InlineData("MeetingRecord.Web.Client/Layout/WasmLayout.razor", "<BackgroundJobProgressPanel", "右下角的進度面板（1.1.15）")]
    public void Layout_ShouldMount(string relativePath, string marker, string purpose)
    {
        // 註解掉的不算——那正是「看起來還在、其實沒有渲染」的樣子。
        var path = Path.Combine(ComponentSourceRoots.SolutionRoot, relativePath);
        var markup = RazorCommentPattern().Replace(File.ReadAllText(path), string.Empty);

        Assert.True(
            markup.Contains(marker, StringComparison.Ordinal),
            $"{relativePath} 少了 {marker}（{purpose}）。所有頁面都以這個版面為底，少了它整站都看不到。");
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorCommentPattern();
}
