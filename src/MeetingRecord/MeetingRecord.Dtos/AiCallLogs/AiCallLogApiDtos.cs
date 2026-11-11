namespace MeetingRecord.Dtos.AiCallLogs;

/// <summary>AI 對話紀錄頁要顯示的設定（1.1.38，<c>GET api/v1/ai-call-logs/meta</c>）。</summary>
public class AiCallLogMetaDto
{
    /// <summary>是否正在記錄（<c>AiCallLogSettings:Enabled</c>）。</summary>
    public bool Enabled { get; set; }

    /// <summary>保留天數（<c>AiCallLogSettings:RetentionDays</c>）。</summary>
    public int RetentionDays { get; set; }

    /// <summary>CSV 最多匯出幾筆；結果達到這個數字時畫面提示。</summary>
    public int MaxExportRows { get; set; }
}

/// <summary>清除某日之前的 AI 對話紀錄（1.1.38，<c>POST api/v1/ai-call-logs/purge</c>）：刪除 <see cref="Before"/> 之前（不含當天）。</summary>
public class AiCallLogPurgeRequestDto
{
    public DateTime Before { get; set; }
}
