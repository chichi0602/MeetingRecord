using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 釘住 1.0.59 的修正：表格勾選欄（AntDesign <c>&lt;Selection&gt;</c>）出不出現，**不可以跟著資料變**。
///
/// <para>
/// AntDesign 1.6.0（1.6.2 也一樣）的 <c>Selection.Dispose</c> 寫的是 <c>Table?.Selection.Type</c>，
/// 表頭那顆 Selection 不在時 <c>Table.Selection</c> 是 null，清掉列上的 Selection 就丟
/// <see cref="NullReferenceException"/>。當年頁面在伺服器執行（Blazor Server），這個例外讓連線當場斷掉、
/// 接著在 finalizer 執行緒再丟一次，**整個伺服器行程跟著當掉**（所有人都斷線，IIS 重啟應用程式集區）。
/// 1.0.53 的會議紀錄頁、專案頁歷史會議、待辦頁用「這頁有沒有可以刪的列」決定要不要畫勾選欄，
/// 進頁面（資料還沒載入）與每次存檔重載都會讓勾選欄一下有一下沒有——當時正式機的「重新連線」與網站重啟就是這個。
/// 現在頁面在瀏覽器執行，不會再拖垮伺服器，但例外照樣會發生，規則維持。
/// </para>
///
/// <para>
/// 規則：<c>&lt;Selection&gt;</c> 要嘛無條件出現，要嘛包在 <c>@if (屬性名)</c> 裡，而且那個屬性只由
/// <c>PermissionView.Can(...)</c> 組成（權限在頁面存活期間不會變）。
/// 不能勾的列用 <c>Disabled</c> 擋，不要用拿掉整欄的方式。
/// </para>
/// </summary>
public sealed partial class TableSelectionColumnTests
{
    [Fact]
    public void SelectionColumn_ShouldNotBeToggledByData()
    {
        var offenders = new List<string>();
        var selectionCount = 0;
        foreach (var razorPath in ComponentSourceRoots.EnumerateFiles("*.razor"))
        {
            var markup = RazorCommentPattern().Replace(File.ReadAllText(razorPath), string.Empty);
            foreach (Match selection in SelectionTagPattern().Matches(markup))
            {
                selectionCount++;
                var before = markup[..selection.Index];
                var lastIf = IfConditionPattern().Matches(before).LastOrDefault();

                // 最近的 @if 要在勾選欄正上方（中間只隔「{」與空白）才算包住它。
                if (lastIf is null || !string.IsNullOrWhiteSpace(before[(lastIf.Index + lastIf.Length)..].Replace("{", string.Empty)))
                {
                    continue;
                }

                var condition = lastIf.Groups["condition"].Value.Trim();
                var codeBehind = File.Exists(razorPath + ".cs") ? File.ReadAllText(razorPath + ".cs") : string.Empty;
                if (!IsPermissionOnly(condition, codeBehind))
                {
                    offenders.Add($"{Path.GetFileName(razorPath)}：@if ({condition})");
                }
            }
        }

        // 1.1.48：掃描範圍錯了時，零個勾選欄也會「通過」。1.1.48 時有 10 個。
        Assert.True(selectionCount >= 8, $"只掃到 {selectionCount} 個勾選欄，掃描範圍可能錯了。");
        Assert.True(
            offenders.Count == 0,
            "以下表格的勾選欄會跟著資料出現／消失，AntDesign 1.6 會在清掉 Selection 時丟 NullReferenceException（Blazor Server 時期會拖垮整個伺服器行程）。"
            + "請改成只看權限（PermissionView.Can）的條件，不能勾的列用 Disabled：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Rule_ShouldRejectDataDrivenConditions_AndAcceptPermissionOnlyOnes()
    {
        // 規則本身的回歸測試：1.0.53 的三種寫法要被擋下，權限屬性要放行。
        const string code = """
            private bool ShowSelection => meetingAdapterModels.Any(CanDelete);
            private bool CanDelete => PermissionView.Can(MagicObjectHelper.角色_分類清單, PermissionActions.Delete);
            private bool CanBatch => PermissionView.Can(A, B)
                || PermissionView.Can(A, C);
            private bool CanDeleteWasm => PermissionView.Can(MagicObjectHelper.角色_分類清單, PermissionActions.Delete);
            private bool CanDeleteOld => AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.角色_分類清單, PermissionActions.Delete);
            private bool ShowWasm => PermissionView.Can(A, B) && rows.Count > 0;
            """;

        Assert.False(IsPermissionOnly("ShowSelection", code));
        Assert.False(IsPermissionOnly("projectMeetings.Any(CanDetach)", code));
        Assert.False(IsPermissionOnly("Missing", code));
        Assert.True(IsPermissionOnly("CanDelete", code));
        Assert.True(IsPermissionOnly("CanBatch", code));
        Assert.True(IsPermissionOnly("CanDeleteWasm", code));
        Assert.False(IsPermissionOnly("ShowWasm", code));
        Assert.False(IsPermissionOnly("CanDeleteOld", code));   // 不是 PermissionView.Can 的權限寫法一律不放行
    }

    private static bool IsPermissionOnly(string condition, string codeBehind)
    {
        if (!IdentifierPattern().IsMatch(condition))
        {
            return false;
        }

        var property = Regex.Match(codeBehind, $@"\bbool\s+{Regex.Escape(condition)}\s*=>\s*(?<expression>[^;]+);");
        if (!property.Success)
        {
            return false;
        }

        var rest = PermissionCallPattern().Replace(property.Groups["expression"].Value, string.Empty);
        return PermissionCallPattern().IsMatch(property.Groups["expression"].Value)
               && LogicOnlyPattern().IsMatch(rest);
    }

    [GeneratedRegex(@"<Selection[\s/>]")]
    private static partial Regex SelectionTagPattern();

    [GeneratedRegex(@"@if\s*\((?<condition>(?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)")]
    private static partial Regex IfConditionPattern();

    [GeneratedRegex(@"PermissionView\.Can\([^()]*\)")]
    private static partial Regex PermissionCallPattern();

    [GeneratedRegex(@"^[\s()!|&]*$")]
    private static partial Regex LogicOnlyPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorCommentPattern();
}
