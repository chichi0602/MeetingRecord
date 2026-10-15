using MeetingRecord.Share.Enums;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// AI 對話紀錄的內容檔（1.0.29）。**屬性名稱就是檔案格式**，改名會讓舊檔讀不回來；新增欄位只能往後加。
///
/// <para>
/// ⚠️ 絕不存放：API 金鑰、HTTP 標頭、端點網址的 query、圖片的原始位元組（只記類型與大小）、音訊。
/// </para>
/// </summary>
public sealed class AiCallLogEntry
{
    public int SchemaVersion { get; set; } = 1;

    public Guid CallId { get; set; }

    public DateTime OccurredAt { get; set; }

    public AiUsageFeature Feature { get; set; }

    /// <summary>功能的中文名稱，讓匯出的 JSON 不必查列舉就看得懂。</summary>
    public string FeatureText { get; set; } = string.Empty;

    public AiUsageOutcome Outcome { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int? UserId { get; set; }

    public string? UserName { get; set; }

    public int? MeetingId { get; set; }

    public int? ProjectId { get; set; }

    public string? RelatedInfo { get; set; }

    public string? ThreadKey { get; set; }

    public AiCallLogRequest Request { get; set; } = new();

    public AiCallLogResponse Response { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public string? ExceptionType { get; set; }

    public int? HttpStatus { get; set; }

    public long ElapsedMilliseconds { get; set; }
}

/// <summary>
/// 送出內容。<see cref="Kind"/> 為 <c>chat</c> 時看 <see cref="Messages"/>／<see cref="Images"/>；
/// 為 <c>transcription</c> 時看檔名、時長與段落（不存音訊本身）。
/// </summary>
public sealed class AiCallLogRequest
{
    public const string ChatKind = "chat";
    public const string TranscriptionKind = "transcription";

    public string Kind { get; set; } = ChatKind;

    public List<AiCallLogMessage> Messages { get; set; } = [];

    public List<AiCallLogImage> Images { get; set; } = [];

    public string? FileName { get; set; }

    public double? AudioSeconds { get; set; }

    public bool IsAudioDurationEstimated { get; set; }

    /// <summary>第幾段（1 起算）。</summary>
    public int? SegmentIndex { get; set; }

    public int? SegmentCount { get; set; }
}

/// <summary>一則送出的訊息。<see cref="Role"/> 為 <c>system</c> 或 <c>user</c>。</summary>
public sealed class AiCallLogMessage
{
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}

/// <summary>隨訊息送出的圖片，只記類型與大小。</summary>
public sealed class AiCallLogImage
{
    public string MediaType { get; set; } = string.Empty;

    public long Bytes { get; set; }
}

/// <summary>收到的回應。</summary>
public sealed class AiCallLogResponse
{
    public string Text { get; set; } = string.Empty;

    /// <summary>串流中途取消或失敗：<see cref="Text"/> 只是已收到的部分。</summary>
    public bool IsPartial { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? CachedInputTokens { get; set; }
}
