using System.Globalization;
using MeetingRecord.Dtos.AiUsage;

namespace MeetingRecord.Web.Export;

/// <summary>
/// Token 用量 CSV 的欄位（1.0.31；1.1.36 從 <c>TokenUsageView.CsvColumns</c> 搬來，匯出改由伺服器產生）。
/// 數字一律 InvariantCulture，金額不加千分位，試算表才算得動。
/// </summary>
public static class AiUsageCsv
{
    public static readonly IReadOnlyList<CsvColumn<AiUsageDetail>> Columns =
    [
        new("時間", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        new("功能", x => x.FeatureText),
        new("結果", x => x.OutcomeText),
        new("使用者", x => x.UserName),
        new("對象", x => x.TargetText),
        new("供應商", x => x.Provider),
        new("模型", x => x.Model),
        new("輸入token", x => x.InputTokens?.ToString(CultureInfo.InvariantCulture)),
        new("輸出token", x => x.OutputTokens?.ToString(CultureInfo.InvariantCulture)),
        new("快取token", x => x.CachedInputTokens?.ToString(CultureInfo.InvariantCulture)),
        new("音訊秒數", x => x.AudioSeconds?.ToString("0.###", CultureInfo.InvariantCulture)),
        new("時長推估", x => x.AudioSeconds is null ? null : x.IsAudioDurationEstimated ? "是" : "否"),
        new("估算金額", x => x.EstimatedCost?.ToString(CultureInfo.InvariantCulture)),
        new("幣別", x => x.Currency),
        new("匯率", x => x.ExchangeRate?.ToString(CultureInfo.InvariantCulture)),
        new("換算金額", x => x.ConvertedCost?.ToString(CultureInfo.InvariantCulture)),
        new("換算幣別", x => x.ConvertedCost is null ? null : x.ConvertedCurrency ?? x.Currency),
        new("錯誤訊息", x => x.ErrorMessage),
        new("呼叫識別碼", x => x.CallId?.ToString()),
    ];
}
