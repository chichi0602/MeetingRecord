using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 頁有「確定」鈕的視窗不得用 <c>@bind-Visible</c>（1.1.19）。
///
/// <para>
/// AntDesign 1.6 的 Modal 按「確定」時，會先送出 <c>VisibleChanged(false)</c> 再呼叫 <c>OnOk</c>。雙向繫結下，
/// 伺服器擋下（400／409，例如名稱重複、改期限沒附原因）時視窗已經關了，處理器裡再設回 true 也打不開，使用者填的內容跟著不見。
/// Blazor Server 頁時序剛好讓它重新打開，所以以前沒出事；WASM 頁每次都會。
/// 單向 <c>Visible="@..."</c> 之下，開關一律由頁面自己決定（存檔成功、取消才關）。
/// </para>
/// </summary>
public sealed class ModalVisibleBindingTests
{
    private static readonly Regex ModalTag = new(@"<Modal\b(?<attrs>.*?)>", RegexOptions.Singleline);

    [Fact]
    public void WasmModalsWithOkButton_ShouldNotUseTwoWayVisibleBinding()
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ComponentSourceRoots.WebClient, "*.razor", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in ModalTag.Matches(source))
            {
                // 去掉 Razor 註解再判斷（說明文字裡會提到 @bind-Visible）。
                var attrs = Regex.Replace(match.Groups["attrs"].Value, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
                if (attrs.Contains("OnOk", StringComparison.Ordinal) && attrs.Contains("@bind-Visible", StringComparison.Ordinal))
                {
                    var line = source[..match.Index].Count(c => c == '\n') + 1;
                    violations.Add($"{Path.GetRelativePath(ComponentSourceRoots.SolutionRoot, file)}:{line}");
                }
            }
        }

        Assert.True(violations.Count == 0, "以下 WASM 視窗有確定鈕卻用 @bind-Visible（伺服器擋下時視窗會被關掉）：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Rule_ShouldCatchTwoWayBindingWithOkButton()
    {
        // 規則本身的回歸案例：有確定鈕＋雙向繫結要抓到；只有說明文字提到、或沒有確定鈕的不算。
        static bool Violates(string razor)
        {
            var attrs = Regex.Replace(ModalTag.Match(razor).Groups["attrs"].Value, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
            return attrs.Contains("OnOk", StringComparison.Ordinal) && attrs.Contains("@bind-Visible", StringComparison.Ordinal);
        }

        Assert.True(Violates("<Modal Title=\"x\" @bind-Visible=\"@open\" OnOk=\"Save\">"));
        Assert.False(Violates("<Modal Title=\"x\" @* 不要用 @bind-Visible *@ Visible=\"@open\" OnOk=\"Save\">"));
        Assert.False(Violates("<Modal Title=\"x\" @bind-Visible=\"@open\" Footer=\"null\">"));
    }
}
