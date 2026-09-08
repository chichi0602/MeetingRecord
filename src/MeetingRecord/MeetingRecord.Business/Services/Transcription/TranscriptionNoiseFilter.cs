namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 濾掉語音轉錄供應商把「自己的系統指令」當成轉錄結果吐回來的段落。
///
/// <para>
/// 這是 <c>gpt-4o-transcribe</c> 在音檔沒有可辨識語音時的已知行為：不回空字串，
/// 而是回一整份英文操作指令（提到 language list、keyword list 等本系統根本沒送的參數
/// ——見 <c>AzureOpenAiTranscriptionProvider.TranscribeAsync</c> 只送了 file 與 response_format）。
/// 這段文字若留在逐字稿裡，不只預覽難看，產生會議紀錄時還會被當成逐字稿餵給 LLM。
/// </para>
///
/// <para>
/// 抽成純函式以便單元測試——與 <c>TranscriptChunker</c>、
/// <c>AzureOpenAiTranscriptionProvider.BuildRequestUri</c> 同一個慣例。
/// </para>
/// </summary>
public static class TranscriptionNoiseFilter
{
    /// <summary>逐字稿的分段接合字串，與 <c>TranscriptionJobRunner.SegmentSeparator</c> 一致。</summary>
    public const string SegmentSeparator = "\n\n";

    /// <summary>
    /// 指令外漏的比對標記，全部取自實際觀察到的外漏內容。
    ///
    /// <para>
    /// 刻意逐段落各留至少一個標記——外漏內容本身是以空行分隔的多個段落，
    /// <see cref="Strip"/> 是逐段落過濾的，只押其中一句會讓其他段落漏網。
    /// 真實的會議逐字稿不會出現這些談論「如何轉錄」的英文句子，誤判機率實質為零。
    /// </para>
    /// </summary>
    private static readonly string[] InstructionLeakMarkers =
    [
        "use this only as weak contextual guidance",
        "the audio itself is authoritative",
        "the language list may help identify speech",
        "preserve code-switching across languages",
        "use the keyword list as contextual transcription hints",
        "do not invent or force terms that were not spoken",
        "if there is no intelligible speech in the audio",
        "do not transcribe non-speech sounds",
        "transcribe only the final user audio message",
    ];

    /// <summary>
    /// 判斷一段文字是否為供應商的指令外漏。命中任一標記即成立。
    /// </summary>
    public static bool IsProviderInstructionLeak(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return InstructionLeakMarkers.Any(
            marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 逐段落濾掉指令外漏後重新接合。
    ///
    /// <para>
    /// 逐段落而非整份判斷——同一份逐字稿可能是多個轉錄分段接起來的，
    /// 其中只有沒有語音的那幾段會外漏，其他段落是真實內容，不能一起丟掉。
    /// 整份都是外漏時（例如純靜音的音檔）回傳空字串，這是正確結果。
    /// </para>
    /// </summary>
    public static string Strip(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return string.Empty;
        }

        var kept = transcript
            .Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(paragraph => !IsProviderInstructionLeak(paragraph))
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => paragraph.Length > 0);

        return string.Join(SegmentSeparator, kept);
    }
}
