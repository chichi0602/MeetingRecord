namespace MeetingRecord.Models.Systems;

/// <summary>
/// 會議影音檔的上傳輸入（比照 <see cref="ProjectUploadFileInput"/>）。
/// </summary>
public class MeetingMediaUploadInput
{
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSize { get; set; }

    public Stream Content { get; set; } = Stream.Null;

    /// <summary>1.1.69：轉錄完自動產生會議紀錄要用的提示詞；null＝不自動產生。</summary>
    public int? AutoDraftPromptTemplateId { get; set; }

    /// <summary>1.1.73：轉錄引擎（已在建立上傳工作階段時驗證過）。</summary>
    public MeetingRecord.Share.Enums.TranscriptionEngine TranscriptionEngine { get; set; }

    /// <summary>1.1.73：要不要分講者；null＝照設定的部署。</summary>
    public bool? TranscriptionDiarize { get; set; }

    /// <summary>1.1.73：LLM Speech 分講者時的講者上限。</summary>
    public int? TranscriptionMaxSpeakers { get; set; }
}
