using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Tests;

public class ApiResultTests
{
    [Fact]
    public void ValidationError_ShouldKeepStructuredErrors()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["Title"] = new[] { "標題不可為空白" }
        };

        var result = ApiResult<object>.ValidationError(errors);

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(result.Errors);
        Assert.Equal("標題不可為空白", result.Errors["Title"][0]);
        Assert.Equal("標題不可為空白", result.ErrorMessage);
    }

    [Fact]
    public void ServerErrorResult_ShouldIncludeExceptionInfo()
    {
        var exception = new InvalidOperationException("測試例外");

        var result = ApiResult<object>.ServerErrorResult("API 呼叫失敗", exception);

        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
        Assert.NotNull(result.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.Exception.Type);
        Assert.Equal("測試例外", result.Exception.Message);
        Assert.Contains("測試例外", result.ErrorDetail);
    }
}
