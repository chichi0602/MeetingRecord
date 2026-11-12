using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Jobs;

/// <summary>背景工作的種類（1.1.15）：API 的 <c>kind</c> 參數與 <see cref="BackgroundJobDto.Kind"/> 用這兩個字串。</summary>
public static class BackgroundJobKinds
{
    /// <summary>語音轉文字（含上傳中）。</summary>
    public const string Transcription = "Transcription";

    /// <summary>產生會議紀錄。</summary>
    public const string Draft = "Draft";

    public static bool IsValid(string? kind) => kind is Transcription or Draft;
}

/// <summary>
/// 右下角背景工作進度面板的一列（1.1.15）。由伺服器把兩個進度通知器的項目轉成同一種形狀，
/// 階段文字也在伺服器組好，前端照著顯示。
/// </summary>
public sealed class BackgroundJobDto
{
    public string Kind { get; set; } = BackgroundJobKinds.Transcription;

    public int MeetingId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int Percent { get; set; }

    /// <summary>階段文字，例如「上傳中」「轉錄中（第 2/5 段）」。</summary>
    public string PhaseText { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public bool IsCompleted { get; set; }

    public bool IsFailed { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>影音檔還在上傳（取消時要用「上傳」種類，不能用「轉錄」）。</summary>
    public bool IsUploading { get; set; }

    /// <summary>能不能顯示「取消」：進行中都可以（1.0.37 起上傳中也可以）。</summary>
    [JsonIgnore]
    public bool CanBeCancelled => IsRunning;

    /// <summary>同一筆會議可能同時有轉錄與生成兩種工作，所以 key 要帶上種類。</summary>
    [JsonIgnore]
    public string Key => $"{Kind}-{MeetingId}";

    [JsonIgnore]
    public string KindText => Kind == BackgroundJobKinds.Transcription ? "語音轉文字" : "產生會議紀錄";

    [JsonIgnore]
    public string KindIcon => Kind == BackgroundJobKinds.Transcription ? "graphic_eq" : "auto_awesome";
}
