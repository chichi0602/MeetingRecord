using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Web.Configuration;

/// <summary>
/// 瀏覽器端錯誤回報（<c>ClientErrorReporting</c> 區段，0.4.122，移植自 NET10-Blazor-Starter）。
///
/// 只在登入後、有 Blazor circuit 的頁面啟用（登入頁等靜態頁面沒有回報管道，匿名者無法灌資料）。
/// 每個 circuit 每分鐘最多 <see cref="MaxPerCircuitPerMinute"/> 筆，超過的丟棄並計數。
/// </summary>
public class ClientErrorReportingSettings
{
    public const string SectionName = "ClientErrorReporting";

    public bool Enabled { get; set; } = true;

    [Range(1, 1000)]
    public int MaxPerCircuitPerMinute { get; set; } = 10;
}
