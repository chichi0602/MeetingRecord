using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// AI 對話紀錄的查詢條件（1.0.30）。比照 <see cref="ExceptionLogQuery"/>：自己的專用查詢型別，不重用 <see cref="DataRequest"/>。
/// </summary>
public class AiCallLogQuery
{
    /// <summary>起始日（含），比對 OccurredAt。</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>結束時間（含），比對 OccurredAt。</summary>
    public DateTime? EndTime { get; set; }

    public AiUsageFeature? Feature { get; set; }

    public AiUsageOutcome? Outcome { get; set; }

    /// <summary>使用者 Id；null 表示不限。</summary>
    public int? UserId { get; set; }

    /// <summary>模型名稱（完全相同）；空字串表示不限。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>關鍵字：比對關聯說明（部分比對）；貼上完整的 CallId 則直接找那一筆。</summary>
    public string Keyword { get; set; } = string.Empty;

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = MagicObjectHelper.PageSize;

    public string SortField { get; set; } = string.Empty;

    public bool? SortDescending { get; set; }
}

/// <summary>篩選下拉的選項：資料庫裡實際出現過的使用者與模型。</summary>
public sealed record AiCallLogFilterOptions(
    IReadOnlyList<(int UserId, string UserName)> Users,
    IReadOnlyList<string> Models);

/// <summary>同一次呼叫在用量帳本裡的那一列（明細窗顯示用量與費用）。</summary>
public sealed record AiCallLogUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens,
    double? AudioSeconds,
    bool IsAudioDurationEstimated,
    decimal? EstimatedCost,
    string? Currency,
    decimal? ExchangeRate,
    string? ConvertedCurrency);
