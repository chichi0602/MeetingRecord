using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Tests;

/// <summary>
/// 轉錄供應商指令外漏過濾器的單元測試。
///
/// 樣本取自實際觀察到的外漏內容：<c>gpt-4o-transcribe</c> 在音檔沒有可辨識語音時，
/// 會把自己的系統指令當成轉錄結果吐回來。
/// </summary>
public sealed class TranscriptionNoiseFilterTests
{
    /// <summary>實際外漏的內容（多個段落以空行分隔）。</summary>
    private const string LeakedInstructions = """
        There may be provided context on the content of the audio or conversation. Use this only as weak contextual guidance. The audio itself is authoritative.

        The language list may help identify speech that appears in the audio. It is not exhaustive, and it must not override what is actually spoken. Preserve code-switching across languages. Do not translate.

        Use the keyword list as contextual transcription hints for terms that may appear in the audio in any language or script. Include a listed term when it is present in the audio, but do not invent or force terms that were not spoken.

        If there is no intelligible speech in the audio, output an empty string. Do not transcribe non-speech sounds unless they are explicitly spoken as words.

        There may be multiple messages in the conversation. Transcribe only the final user audio message.
        """;

    #region 命中判斷

    [Theory]
    [InlineData("There may be provided context. Use this only as weak contextual guidance.")]
    [InlineData("The audio itself is authoritative.")]
    [InlineData("The language list may help identify speech that appears in the audio.")]
    [InlineData("Preserve code-switching across languages. Do not translate.")]
    [InlineData("Use the keyword list as contextual transcription hints for terms.")]
    [InlineData("Include a listed term, but do not invent or force terms that were not spoken.")]
    [InlineData("If there is no intelligible speech in the audio, output an empty string.")]
    [InlineData("Do not transcribe non-speech sounds unless they are explicitly spoken.")]
    [InlineData("Transcribe only the final user audio message.")]
    public void IsProviderInstructionLeak_ShouldMatchEveryKnownParagraph(string paragraph)
    {
        // 外漏內容是多個段落，Strip 逐段落過濾，所以每一段都要有對應的標記，否則會漏網。
        Assert.True(TranscriptionNoiseFilter.IsProviderInstructionLeak(paragraph));
    }

    [Fact]
    public void IsProviderInstructionLeak_ShouldIgnoreCase()
    {
        Assert.True(TranscriptionNoiseFilter.IsProviderInstructionLeak(
            "THE AUDIO ITSELF IS AUTHORITATIVE."));
    }

    [Theory]
    [InlineData("今天的會議由王經理主持，主要討論第三季的產品改版時程。")]
    [InlineData("Let's move on to the next agenda item and review the quarterly numbers.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsProviderInstructionLeak_ShouldNotFlagRealContent(string? text)
    {
        Assert.False(TranscriptionNoiseFilter.IsProviderInstructionLeak(text));
    }

    #endregion

    #region 逐段落過濾

    [Fact]
    public void Strip_WholeTranscriptIsLeak_ShouldReturnEmpty()
    {
        // 純靜音的音檔就是這種情況：整份都是外漏，過濾後本來就該是空的。
        Assert.Equal(string.Empty, TranscriptionNoiseFilter.Strip(LeakedInstructions));
    }

    [Fact]
    public void Strip_ShouldKeepRealSegmentsAndDropLeakedOnes()
    {
        // 一份逐字稿是多個轉錄分段接起來的，只有沒有語音的那幾段會外漏。
        var transcript = string.Join(
            TranscriptionNoiseFilter.SegmentSeparator,
            "第一段：確認需求範圍與交付日期。",
            LeakedInstructions,
            "第三段：下次會議訂在下週三下午兩點。");

        var stripped = TranscriptionNoiseFilter.Strip(transcript);

        Assert.Equal(
            "第一段：確認需求範圍與交付日期。\n\n第三段：下次會議訂在下週三下午兩點。",
            stripped);
    }

    [Fact]
    public void Strip_CleanTranscript_ShouldRemainUnchanged()
    {
        const string transcript = "第一段內容。\n\n第二段內容。";

        Assert.Equal(transcript, TranscriptionNoiseFilter.Strip(transcript));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n   ")]
    public void Strip_BlankInput_ShouldReturnEmpty(string? transcript)
    {
        Assert.Equal(string.Empty, TranscriptionNoiseFilter.Strip(transcript));
    }

    #endregion
}
