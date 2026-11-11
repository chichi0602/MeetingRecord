namespace MeetingRecord.Dtos.Diagnostics;

/// <summary>日誌等級的現況（1.1.33，<c>GET api/v1/monitoring/log-level</c>）。只在執行期有效，重新啟動回到 nlog.config 的值。</summary>
public class LogLevelStateDto
{
    /// <summary>讀得到 nlog.config 的 <c>&lt;logger name="*"&gt;</c> 規則才能調整。</summary>
    public bool IsAvailable { get; set; }

    public LogLevelRank EffectiveLevel { get; set; } = LogLevelRank.Info;

    public LogLevelRank SystemDefaultLevel { get; set; } = LogLevelRank.Info;

    /// <summary>目前是否有執行期覆寫。</summary>
    public bool HasOverride { get; set; }
}

/// <summary>套用日誌等級（1.1.33，<c>PUT api/v1/monitoring/log-level</c>）。只接受 Trace～Fatal。</summary>
public class LogLevelApplyRequestDto
{
    public LogLevelRank Level { get; set; }
}
