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
}
