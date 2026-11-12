using MeetingRecord.Dtos.AiCallLogs;

namespace MeetingRecord.Web.Export;

/// <summary>
/// AI 對話紀錄 CSV 的欄位（1.0.30；1.1.38 起匯出由伺服器產生）。
/// 只有中繼資料，**不含送出內容與回應**（那是逐字稿等敏感全文）。
/// </summary>
public static class AiCallLogCsv
{
    public static readonly IReadOnlyList<CsvColumn<AiCallLogAdapterModel>> Columns =
    [
        new("時間", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("功能", x => x.FeatureText),
        new("結果", x => x.OutcomeText),
        new("供應商", x => x.Provider),
        new("模型", x => x.Model),
        new("使用者", x => x.UserName),
        new("會議Id", x => x.MeetingId?.ToString()),
        new("專案Id", x => x.ProjectId?.ToString()),
        new("關聯說明", x => x.RelatedInfo),
        new("HTTP", x => x.HttpStatus?.ToString()),
        new("耗時ms", x => x.ElapsedMilliseconds.ToString()),
        new("送出字元", x => x.RequestCharacters.ToString()),
        new("回應字元", x => x.ResponseCharacters.ToString()),
        new("錯誤", x => x.ErrorMessage),
        new("呼叫識別碼", x => x.CallId.ToString()),
    ];
}
