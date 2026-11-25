namespace MeetingRecord.Share.Enums;

/// <summary>
/// 1.1.73：影音轉文字稿用的引擎，上傳／重新轉錄時由使用者選，記在會議上（<c>Meeting.TranscriptionEngine</c>）。
/// 以 int 儲存於資料庫，顯示文字集中在 <see cref="TranscriptionEngineText.Describe"/>。
/// </summary>
public enum TranscriptionEngine
{
    /// <summary>Azure OpenAI 的 audio/transcriptions（分講者用 diarize 部署，不分講者用另一個部署）。</summary>
    AzureOpenAI = 0,

    /// <summary>Azure AI Speech 的 LLM Speech（Fast Transcription API 開 enhancedMode），整個檔案一次送、可分講者。</summary>
    AzureLlmSpeech = 1,
}

/// <summary>轉錄引擎的顯示文字。</summary>
public static class TranscriptionEngineText
{
    public static string Describe(TranscriptionEngine engine) => engine switch
    {
        TranscriptionEngine.AzureOpenAI => "Azure OpenAI",
        TranscriptionEngine.AzureLlmSpeech => "Azure LLM Speech",
        _ => engine.ToString(),
    };

    public static string Describe(int engine) => Describe((TranscriptionEngine)engine);

    /// <summary>引擎＋是否分講者，例如「Azure LLM Speech（分講者）」。<paramref name="diarize"/> 為 null＝照當時的部署。</summary>
    public static string Describe(int engine, bool? diarize) => diarize switch
    {
        true => $"{Describe(engine)}（分講者）",
        false => $"{Describe(engine)}（不分講者）",
        _ => Describe(engine),
    };
}
