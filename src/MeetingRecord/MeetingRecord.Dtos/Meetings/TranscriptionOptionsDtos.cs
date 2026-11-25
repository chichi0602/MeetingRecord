namespace MeetingRecord.Dtos.Meetings;

/// <summary>
/// 1.1.73：上傳／重新轉錄時選的轉錄方式。整個物件為 null＝沿用設定的部署（1.1.72 以前的行為）。
/// </summary>
public class TranscriptionOptionsDto
{
    /// <summary><c>TranscriptionEngine</c>：0＝Azure OpenAI，1＝Azure LLM Speech。</summary>
    public int Engine { get; set; }

    /// <summary>要不要分講者。</summary>
    public bool Diarize { get; set; } = true;

    /// <summary>LLM Speech 分講者時的講者上限（2～35）；其他情況忽略。</summary>
    public int? MaxSpeakers { get; set; }
}

/// <summary>1.1.73：確認框要畫哪些選項、預設選什麼（<c>GET api/v1/meeting/transcription-options</c>）。</summary>
public class TranscriptionOptionsMetaDto
{
    /// <summary>Azure OpenAI 能分講者（轉錄部署是 diarize）。</summary>
    public bool OpenAiDiarizeAvailable { get; set; }

    /// <summary>Azure OpenAI 能不分講者（轉錄部署不是 diarize，或另設了不分講者的部署）。</summary>
    public bool OpenAiPlainAvailable { get; set; }

    /// <summary>Azure LLM Speech 設定好了（Endpoint 與 ApiKey）。</summary>
    public bool LlmSpeechAvailable { get; set; }

    public int DefaultEngine { get; set; }

    public bool DefaultDiarize { get; set; } = true;

    /// <summary>講者上限的預設值：專案與會人員名冊的人數，沒有專案或名冊是空的時用設定值。</summary>
    public int SuggestedMaxSpeakers { get; set; }

    public int MinSpeakers { get; set; }

    public int MaxSpeakers { get; set; }

    /// <summary>LLM Speech 單次送出的長度上限（秒），超過會切段、講者各段分開標示。</summary>
    public int LlmSpeechMaxFileSeconds { get; set; }
}
