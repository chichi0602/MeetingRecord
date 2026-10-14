using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 例外合併鍵。
///
/// 這支測試守住整個「系統例外紀錄」最關鍵的一件事：合併鍵必須用日誌訊息的**樣板**
/// （<c>Name={CategoryName}</c>），不能用算好的訊息（<c>Name=技術文件</c>）。
/// 用錯的話，每個分類名稱都會變成一個新簽章，列數立刻失控 ——
/// 而這個症狀要等到正式環境累積一陣子才看得出來。
/// </summary>
public sealed class ExceptionSignatureTests
{
    [Fact]
    public void Compute_SameInputs_ShouldProduceSameSignature()
    {
        var first = ExceptionSignature.Compute(
            "System.NullReferenceException", "物件參照未設定", "/categories", "Failed to create category.");
        var second = ExceptionSignature.Compute(
            "System.NullReferenceException", "物件參照未設定", "/categories", "Failed to create category.");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_ShouldReturn64CharLowerHex()
    {
        var signature = ExceptionSignature.Compute("System.Exception", "boom", "/x", "op");

        Assert.Equal(64, signature.Length);
        Assert.All(signature, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Theory]
    // 同一個例外出現在不同頁面，是兩個不同的問題，不可合併。
    [InlineData("System.NullReferenceException", "boom", "/projects", "op")]
    // 同一頁的不同按鈕（操作）亦然。
    [InlineData("System.NullReferenceException", "boom", "/categories", "Failed to delete category.")]
    // 類型不同當然不同。
    [InlineData("System.InvalidOperationException", "boom", "/categories", "op")]
    // 訊息不同亦然。
    [InlineData("System.NullReferenceException", "different", "/categories", "op")]
    public void Compute_AnyComponentDiffers_ShouldProduceDifferentSignature(
        string exceptionType, string message, string page, string operation)
    {
        var baseline = ExceptionSignature.Compute(
            "System.NullReferenceException", "boom", "/categories", "op");

        var other = ExceptionSignature.Compute(exceptionType, message, page, operation);

        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Compute_MessageTemplateVersusFormattedMessage_ShouldDiffer()
    {
        // 這正是「取錯就會列數爆炸」的那一組對照：
        // 樣板只有一種，算好的訊息每個參數值都是一種。
        var template = ExceptionSignature.Compute(
            "System.Exception", "boom", "/categories", "Failed to create category. Name={CategoryName}");

        var formattedA = ExceptionSignature.Compute(
            "System.Exception", "boom", "/categories", "Failed to create category. Name=技術文件");
        var formattedB = ExceptionSignature.Compute(
            "System.Exception", "boom", "/categories", "Failed to create category. Name=會議記錄");

        Assert.NotEqual(template, formattedA);
        Assert.NotEqual(formattedA, formattedB);
    }

    [Fact]
    public void Compute_NullPageAndOperation_ShouldNotThrow()
    {
        var signature = ExceptionSignature.Compute("System.Exception", "boom", null, null);

        Assert.Equal(64, signature.Length);
    }

    [Fact]
    public void Compute_ShouldNotCollideWhenComponentsShift()
    {
        // 以 \n 串接可避免「頁面尾巴被誤讀成操作開頭」這類邊界碰撞。
        var first = ExceptionSignature.Compute("T", "M", "/a", "b");
        var second = ExceptionSignature.Compute("T", "M", "/ab", string.Empty);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TruncateMessage_LongMessage_ShouldBeCapped()
    {
        var message = new string('x', ExceptionSignature.MaxMessageLength + 500);

        var truncated = ExceptionSignature.TruncateMessage(message);

        Assert.Equal(ExceptionSignature.MaxMessageLength, truncated.Length);
    }

    [Fact]
    public void TruncateMessage_NullOrShort_ShouldPassThrough()
    {
        Assert.Equal(string.Empty, ExceptionSignature.TruncateMessage(null));
        Assert.Equal("short", ExceptionSignature.TruncateMessage("short"));
    }
}
