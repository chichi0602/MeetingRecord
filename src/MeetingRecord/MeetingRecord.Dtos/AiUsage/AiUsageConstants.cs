namespace MeetingRecord.Dtos.AiUsage;

/// <summary>Token 用量的上限值（1.1.37，伺服器的 <c>AiUsageAnalysisService</c> 與 WASM 頁共用同一份）。</summary>
public static class AiUsageConstants
{
    /// <summary>每日統計補齊沒有資料的日子，區間最多幾天；超過只列出有資料的日子。</summary>
    public const int MaxFilledDays = 92;

    /// <summary>CSV 最多匯出幾筆（最新的優先）。</summary>
    public const int MaxExportRows = 10_000;
}
