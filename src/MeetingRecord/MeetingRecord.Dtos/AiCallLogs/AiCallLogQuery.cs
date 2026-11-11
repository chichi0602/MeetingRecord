using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

// 1.1.38（WASM 遷移 M5）：從 Models 搬來，AI 對話紀錄 API 與 WASM 頁共用。
namespace MeetingRecord.Dtos.AiCallLogs;

/// <summary>
/// AI 對話紀錄的查詢條件（1.0.30）。比照 <c>ExceptionLogQuery</c>：自己的專用查詢型別，不重用 <c>DataRequest</c>。
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
/// <remarks>1.1.38 起使用者改用 <see cref="AiCallLogUserOption"/>（以前是 tuple，JSON 序列化不出來）。</remarks>
public sealed record AiCallLogFilterOptions(
    IReadOnlyList<AiCallLogUserOption> Users,
    IReadOnlyList<string> Models);

/// <summary>篩選下拉的一位使用者（1.1.38）。</summary>
public sealed record AiCallLogUserOption(int UserId, string UserName);

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
