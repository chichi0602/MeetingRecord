using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 釘住 1.0.62 的修正（M0 基準端對端測試發現）：
/// <list type="number">
/// <item>表單視窗裡的下拉與日期欄外層一定要擋 Enter（<c>@onkeydown:stopPropagation</c>）——否則在日期欄打完日期按 Enter、
/// 或在下拉的搜尋框按 Enter 選項目，會被外層表單當成「送出」，打的值還沒套用就存檔了。</item>
/// <item>Enter 與「確定」一律走 <c>FormSubmitGate</c>——以前 Enter 先 <c>Task.Delay(200)</c> 等回寫、等候不在存檔防護裡，
/// 等候期間再按「確定」或長按 Enter 會新增兩筆。</item>
/// </list>
/// </summary>
public sealed partial class FormEnterGuardTests
{
    /// <summary>
    /// 有 Enter 送出的 CRUD 表單（方案根目錄下的相對路徑，不含副檔名）。
    /// 1.1.7 起搬到 WASM 的頁面在 <c>MeetingRecord.Web.Client/Pages</c>，搬家時改這裡的路徑、不要拿掉。
    /// </summary>
    public static TheoryData<string> CrudForms =>
    [
        "MeetingRecord.Web/Components/Views/Todos/TodoViewView",
        "MeetingRecord.Web/Components/Views/Meetings/MeetingViewView",
        "MeetingRecord.Web/Components/Views/Projects/ProjectViewView",
        "MeetingRecord.Web.Client/Pages/Categories/CategoryPage",
        "MeetingRecord.Web.Client/Pages/Teams/TeamPage",
        "MeetingRecord.Web.Client/Pages/PromptTemplates/PromptTemplatePage",
        "MeetingRecord.Web.Client/Pages/Admins/MyUserPage",
        "MeetingRecord.Web.Client/Pages/Admins/RoleViewPage",
    ];

    [Theory]
    [MemberData(nameof(CrudForms))]
    public void SelectAndDateFields_InFormModal_ShouldStopEnterFromSubmitting(string form)
    {
        var markup = RazorCommentPattern().Replace(File.ReadAllText(ViewPath(form + ".razor")), string.Empty);
        var modal = FormModalPattern().Match(markup);
        Assert.True(modal.Success, $"{form}.razor 找不到 <Modal Title=\"@modalTitle\"> 表單視窗");

        var offenders = new List<string>();
        foreach (Match field in FieldPattern().Matches(modal.Value))
        {
            var before = modal.Value[..field.Index].TrimEnd();
            if (!before.EndsWith("<div @onkeydown:stopPropagation=\"true\">", StringComparison.Ordinal))
            {
                var line = modal.Value[..field.Index].Count(c => c == '\n') + 1;
                offenders.Add($"表單視窗第 {line} 行的 {field.Value.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{form}.razor 的下拉／日期欄外層沒有擋 Enter，請包一層 <div @onkeydown:stopPropagation=\"true\">（照專案表單）：\n" + string.Join("\n", offenders));
    }

    [Theory]
    [MemberData(nameof(CrudForms))]
    public void EnterAndOk_ShouldShareTheSubmitGate(string form)
    {
        var code = File.ReadAllText(ViewPath(form + ".razor.cs"));

        // 「確定」與 Enter 都要經過同一個閘門；Enter 的等候用閘門的 EnterSettleDelay，不能自己 Task.Delay。
        Assert.Contains("private readonly FormSubmitGate submitGate", code);
        Assert.Matches(@"OnModalOKHandleAsync\(MouseEventArgs args\)\s*=>\s*submitGate\.RunAsync\(", code);
        Assert.Matches(@"submitGate\.RunAsync\(\w+, FormSubmitGate\.EnterSettleDelay\)", code);
        Assert.DoesNotContain("Task.Delay(200)", code);
    }

    [Fact]
    public void Scanner_ShouldFlagFieldsWithoutTheWrapper()
    {
        // 規則本身的回歸：沒包的要被抓到、包了的放行。
        const string markup = """
            <Modal Title="@modalTitle">
                <FormItem Label="截止日">
                    <DatePicker TValue="DateTime?" />
                </FormItem>
                <FormItem Label="專案">
                    <div @onkeydown:stopPropagation="true">
                        <Select TItem="int" />
                    </div>
                </FormItem>
            </Modal>
            """;
        var modal = FormModalPattern().Match(markup).Value;
        var unguarded = FieldPattern().Matches(modal)
            .Where(m => !modal[..m.Index].TrimEnd().EndsWith("<div @onkeydown:stopPropagation=\"true\">", StringComparison.Ordinal))
            .Select(m => m.Value.Trim())
            .ToList();

        Assert.Equal(["<DatePicker"], unguarded);
    }

    [GeneratedRegex(@"<Modal Title=""@modalTitle"".*?</Modal>", RegexOptions.Singleline)]
    private static partial Regex FormModalPattern();

    [GeneratedRegex(@"<(Select|DatePicker|RangePicker)(?=[\s/>])")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorCommentPattern();

    private static string ViewPath(string relative)
        => Path.Combine(ComponentSourceRoots.SolutionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
}
