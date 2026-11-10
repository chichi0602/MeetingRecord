namespace MeetingRecord.Dtos.Systems;

/// <summary>「關於」對話窗的內容（1.1.6，<c>GET /api/v1/system/about</c>）。</summary>
public class SystemAboutDto
{
    public string SystemName { get; set; } = string.Empty;

    public string SystemDescription { get; set; } = string.Empty;

    public string SystemVersion { get; set; } = string.Empty;

    public string EnvironmentName { get; set; } = string.Empty;

    public string FrameworkDescription { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>已運作時間（伺服器在回應當下計算）。</summary>
    public TimeSpan Uptime { get; set; }
}
