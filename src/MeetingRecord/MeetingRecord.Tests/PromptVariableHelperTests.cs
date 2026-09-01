using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

public sealed class PromptVariableHelperTests
{
    [Fact]
    public void KnownVariables_ShouldMatchPrdContract()
    {
        // 與 docs/prd/會議紀錄提示詞-prd.md 的固定變數集綁定，變更時兩邊必須同步。
        Assert.Equal(["transcript", "meetingTitle", "meetingDate"], PromptVariableHelper.KnownVariables);
    }

    [Fact]
    public void FindUnknownVariables_WithKnownVariablesOnly_ShouldReturnEmpty()
    {
        var content = "標題：{{meetingTitle}}（{{meetingDate}}）\n逐字稿：{{transcript}}";

        var result = PromptVariableHelper.FindUnknownVariables(content);

        Assert.Empty(result);
    }

    [Fact]
    public void FindUnknownVariables_WithUnknownVariable_ShouldReturnIt()
    {
        var content = "請標註 {{speaker}} 並依 {{transcript}} 整理。";

        var result = PromptVariableHelper.FindUnknownVariables(content);

        Assert.Equal(["speaker"], result);
    }

    [Fact]
    public void FindUnknownVariables_ShouldDeduplicate()
    {
        var content = "{{speaker}} 與 {{speaker}} 以及 {{attendees}}";

        var result = PromptVariableHelper.FindUnknownVariables(content);

        Assert.Equal(["speaker", "attendees"], result);
    }

    [Fact]
    public void FindUnknownVariables_WithInnerWhitespace_ShouldStillMatchKnown()
    {
        var content = "{{ transcript }} 與 {{  meetingTitle  }}";

        var result = PromptVariableHelper.FindUnknownVariables(content);

        Assert.Empty(result);
    }

    [Fact]
    public void FindUnknownVariables_ShouldIgnoreCaseWhenMatchingKnown()
    {
        var content = "{{TRANSCRIPT}}";

        var result = PromptVariableHelper.FindUnknownVariables(content);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindUnknownVariables_WithNullOrWhitespace_ShouldReturnEmpty(string? content)
    {
        var result = PromptVariableHelper.FindUnknownVariables(content);

        Assert.Empty(result);
    }

    [Fact]
    public void FindUnknownVariables_WithoutAnyPlaceholder_ShouldReturnEmpty()
    {
        var result = PromptVariableHelper.FindUnknownVariables("請整理這場會議的決議事項。");

        Assert.Empty(result);
    }

    [Fact]
    public void DescribeKnownVariables_ShouldRenderAllPlaceholders()
    {
        var result = PromptVariableHelper.DescribeKnownVariables();

        Assert.Equal("{{transcript}}、{{meetingTitle}}、{{meetingDate}}", result);
    }

    #region Render（產生會議紀錄時的代入端）

    [Fact]
    public void Render_ShouldSubstituteKnownPlaceholders()
    {
        var result = PromptVariableHelper.Render(
            "會議「{{meetingTitle}}」於 {{meetingDate}} 舉行。逐字稿：{{transcript}}",
            new Dictionary<string, string?>
            {
                ["transcript"] = "今天討論了三件事。",
                ["meetingTitle"] = "需求確認會議",
                ["meetingDate"] = "2026/08/28",
            });

        Assert.Equal("會議「需求確認會議」於 2026/08/28 舉行。逐字稿：今天討論了三件事。", result);
    }

    [Fact]
    public void Render_ShouldIgnoreCaseAndInnerWhitespace()
    {
        var result = PromptVariableHelper.Render(
            "{{ MeetingTitle }} / {{TRANSCRIPT}}",
            new Dictionary<string, string?> { ["meetingTitle"] = "週會", ["transcript"] = "內容" });

        Assert.Equal("週會 / 內容", result);
    }

    [Fact]
    public void Render_ShouldKeepPlaceholder_WhenValueIsNotSupplied()
    {
        // 悄悄代入空字串會讓範本作者看不出哪裡沒生效，因此原樣保留。
        var result = PromptVariableHelper.Render(
            "{{meetingTitle}} 與 {{somethingElse}}",
            new Dictionary<string, string?> { ["meetingTitle"] = "週會" });

        Assert.Equal("週會 與 {{somethingElse}}", result);
    }

    [Fact]
    public void Render_ShouldSubstituteEmpty_WhenValueIsNull()
    {
        var result = PromptVariableHelper.Render(
            "日期：{{meetingDate}}。",
            new Dictionary<string, string?> { ["meetingDate"] = null });

        Assert.Equal("日期：。", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_ShouldReturnEmpty_WhenContentIsNullOrEmpty(string? content)
    {
        var result = PromptVariableHelper.Render(content, new Dictionary<string, string?>());

        Assert.Equal(string.Empty, result);
    }

    #endregion
}
