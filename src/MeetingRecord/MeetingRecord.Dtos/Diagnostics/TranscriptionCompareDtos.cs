namespace MeetingRecord.Dtos.Diagnostics;

/// <summary>
/// 轉錄比較（1.1.72，AI 管理 → 轉錄比較，只限管理者）：同一段片段用幾組部署／參數各轉一次，並排比較。
/// 不碰會議本身（狀態、逐字稿都不變），費用照常記入用量帳本（功能別「轉錄比較」）。
/// </summary>
public class TranscriptionCompareRequestDto
{
    public const int MaxVariants = 4;
    public const int MinSeconds = 30;
    public const int MaxSeconds = 300;

    public int MeetingId { get; set; }

    /// <summary>片段起點（秒）。</summary>
    public int StartSeconds { get; set; }

    /// <summary>片段長度（秒），<see cref="MinSeconds"/>～<see cref="MaxSeconds"/>。</summary>
    public int Seconds { get; set; } = MaxSeconds;

    public List<TranscriptionCompareVariantDto> Variants { get; set; } = [];
}

/// <summary>一組比較條件。空白＝沿用設定（部署）或不送（語言、提示詞）。</summary>
public class TranscriptionCompareVariantDto
{
    public string Deployment { get; set; } = string.Empty;

    /// <summary>ISO-639-1，例如 zh；空白＝不指定。</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>專有名詞提示；diarize 模型不支援，不會送出。</summary>
    public string Prompt { get; set; } = string.Empty;
}

/// <summary>可以挑的會議（有影音檔的，最新的在前）。</summary>
public class TranscriptionCompareMeetingDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime? MeetingDate { get; set; }

    public string MediaFileName { get; set; } = string.Empty;

    public double? MediaDurationSeconds { get; set; }
}

/// <summary>頁面初始資料：目前設定的轉錄部署（預設第一組）與候選會議。</summary>
public class TranscriptionCompareMetaDto
{
    public string ConfiguredDeployment { get; set; } = string.Empty;

    public List<TranscriptionCompareMeetingDto> Meetings { get; set; } = [];
}

/// <summary>每組的預估費用（新台幣）；沒有單價或匯率時是 null。</summary>
public class TranscriptionCompareEstimateDto
{
    public List<decimal?> VariantTwd { get; set; } = [];

    /// <summary>全部估得出來時的合計；有任何一組估不出來就是 null。</summary>
    public decimal? TotalTwd { get; set; }
}

/// <summary>串流事件（一行一筆 JSON）。先一筆 <see cref="Excerpt"/>，每組完成一筆 <see cref="Result"/>，最後 <see cref="Done"/>。</summary>
public class TranscriptionCompareEventDto
{
    public const string Excerpt = "excerpt";
    public const string Result = "result";
    public const string Done = "done";
    public const string Error = "error";

    public string Type { get; set; } = Result;

    /// <summary>片段音檔（<c>data:audio/mpeg;base64,…</c>），只在 <see cref="Excerpt"/>。</summary>
    public string? AudioDataUrl { get; set; }

    /// <summary>片段實際長度（秒），只在 <see cref="Excerpt"/>。</summary>
    public double? ExcerptSeconds { get; set; }

    /// <summary>第幾組（從 0 起），只在 <see cref="Result"/>。</summary>
    public int Index { get; set; }

    public bool Success { get; set; }

    public string Deployment { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public int CharCount { get; set; }

    /// <summary>英文字母占全部字元（不含空白、標點）的比例，0～1。</summary>
    public double LatinRatio { get; set; }

    public double ElapsedSeconds { get; set; }

    public decimal? CostTwd { get; set; }

    public string? Message { get; set; }
}
