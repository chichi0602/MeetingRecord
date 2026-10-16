using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 逐字稿簡轉繁（1.0.41）。用真的 OpenCC 字典跑，順便驗證字典有跟著複製到輸出目錄——
/// 少了 Dictionary 資料夾，部署後第一次讀逐字稿就會丟例外。
/// </summary>
public sealed class ChineseTextConverterTests
{
    [Theory]
    [InlineData("他说现在没有办法", "他說現在沒有辦法")]
    [InlineData("这个东西我觉得会比较麻烦", "這個東西我覺得會比較麻煩")]
    [InlineData("我礼拜五之后才做完", "我禮拜五之後才做完")]
    public void ToTraditional_ShouldConvertSimplifiedCharacters(string input, string expected)
        => Assert.Equal(expected, ChineseTextConverter.ToTraditional(input));

    [Fact]
    public void ToTraditional_ShouldUseTaiwanVariants()
        // 台灣字形：「裡」而不是 OpenCC 標準的「裏」。
        => Assert.Equal("在裡面", ChineseTextConverter.ToTraditional("在里面"));

    [Fact]
    public void ToTraditional_ShouldNotConvertPhrases()
    {
        // 使用者決定只轉字形：逐字稿要忠於原話，不把「软件」換成台灣用詞「軟體」。
        Assert.Equal("軟件", ChineseTextConverter.ToTraditional("软件"));
    }

    [Fact]
    public void ToTraditional_ShouldKeepTraditionalAndMixedText()
    {
        const string text = "已經是繁體的會議紀錄，order code 與 David 保持原樣。";

        Assert.Equal(text, ChineseTextConverter.ToTraditional(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToTraditional_ShouldReturnBlankAsIs(string? input)
        => Assert.Equal(input, ChineseTextConverter.ToTraditional(input));
}
