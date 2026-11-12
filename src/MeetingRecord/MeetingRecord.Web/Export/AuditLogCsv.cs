using MeetingRecord.Dtos.AuditLogs;

namespace MeetingRecord.Web.Export;

/// <summary>稽核紀錄 CSV 的欄位（1.0.32；1.1.43 起匯出由伺服器產生）。來源 IP 放最後。</summary>
public static class AuditLogCsv
{
    public static readonly IReadOnlyList<CsvColumn<AuditLogAdapterModel>> Columns =
    [
        new("發生時間（本地）", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("結果", x => x.SuccessText),
        new("動作名稱", x => x.ActionDisplayName),
        new("動作代碼", x => x.Action),
        new("操作者帳號", x => x.ActorAccount),
        new("操作者Id", x => x.ActorUserId?.ToString()),
        new("目標類型", x => x.TargetType),
        new("目標識別", x => x.TargetId),
        new("摘要", x => x.Detail),
        new("來源IP", x => x.ClientIp),
    ];
}
