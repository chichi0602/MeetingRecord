using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Web.Configuration;

/// <summary>
/// 瀏覽器端錯誤回報（<c>ClientErrorReporting</c> 區段，0.4.122，移植自 NET10-Blazor-Starter）。
///
/// 只有登入者能回報（1.1.47 起經 <c>POST api/v1/client-errors</c>，匿名者 401，無法灌資料）。
/// 每個帳號每分鐘最多 <see cref="MaxPerCircuitPerMinute"/> 筆，超過的丟棄並計數。
/// </summary>
public class ClientErrorReportingSettings
{
    public const string SectionName = "ClientErrorReporting";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 每個帳號每分鐘最多幾筆。名稱沿用 Blazor Server 時代（當時是每條 circuit），避免改到已部署的設定檔；1.1.47 起以帳號計。
    /// </summary>
    [Range(1, 1000)]
    public int MaxPerCircuitPerMinute { get; set; } = 10;
}
