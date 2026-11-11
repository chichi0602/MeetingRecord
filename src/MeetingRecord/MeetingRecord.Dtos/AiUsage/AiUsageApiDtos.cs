namespace MeetingRecord.Dtos.AiUsage;

/// <summary>
/// Token 用量頁上方（1.1.36，<c>GET api/v1/ai-usage/overview</c>）：本月概況、上限列，以及頁面要顯示的設定。
/// 上限讀不到時 <see cref="GlobalQuota"/> 是 null、<see cref="TopUsers"/> 是空的，不擋整頁（同畫面）。
/// </summary>
public class AiUsageOverviewDto
{
    public AiUsageSummary? Summary { get; set; }

    public AiQuotaStatus? GlobalQuota { get; set; }

    public List<AiQuotaUserUsage> TopUsers { get; set; } = [];

    /// <summary>每人每月上限（新台幣）；0＝不限制。</summary>
    public int PerUserLimit { get; set; }

    /// <summary>用量紀錄保留天數（<c>LogRetentionSettings:AiUsageLogDays</c>）；0＝永久保留。</summary>
    public int RetentionDays { get; set; }

    /// <summary>CSV 最多匯出幾筆；篩選結果達到這個數字時畫面提示「只匯出最新 N 筆」。</summary>
    public int MaxExportRows { get; set; }
}

/// <summary>單筆用量的明細（1.1.36，<c>GET api/v1/ai-usage/{id}</c>）。</summary>
public class AiUsageDetailResponseDto
{
    public AiUsageDetail? Detail { get; set; }

    /// <summary>同一次呼叫的 AI 對話紀錄 Id；null 表示沒有（舊紀錄、已清除或當時停用記錄）。</summary>
    public int? CallLogId { get; set; }
}

/// <summary>清除某日之前的用量紀錄（1.1.36，<c>POST api/v1/ai-usage/purge</c>）：刪除 <see cref="Before"/> 之前（不含當天）。</summary>
public class AiUsagePurgeRequestDto
{
    public DateTime Before { get; set; }
}
