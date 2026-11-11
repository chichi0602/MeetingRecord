namespace MeetingRecord.Dtos.AuditLogs;

/// <summary>稽核紀錄頁要顯示的設定（1.1.43，<c>GET api/v1/audit-logs/meta</c>）。</summary>
public class AuditLogMetaDto
{
    /// <summary>自動清除天數（<c>LogRetentionSettings:AuditLogDays</c>），0 表示不自動清除。</summary>
    public int RetentionDays { get; set; }

    /// <summary>「清除 N 天前」的 N（<c>LogRetentionSettings:ManualAuditLogDays</c>）。</summary>
    public int PurgeDays { get; set; }

    /// <summary>CSV 一次最多幾筆。</summary>
    public int MaxExportRows { get; set; }
}
