using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Web.Configuration;

/// <summary>
/// Web API 限流設定（1.0.63）。
///
/// <para>
/// ⚠️ 1.0.62 以前寫死「全站共用一個桶、每分鐘 120 次」：所有使用者與外部用戶端互搶同一份額度，
/// 一個人批次呼叫就會讓其他人一起收到 429。現在依使用者分開計算——已登入的依帳號、未登入的依來源 IP。
/// </para>
///
/// <para>
/// 只套用在 Web API（<c>MapControllers().RequireRateLimiting("api")</c>）；靜態檔與 <c>/hubs/app</c> 推播通道不受影響，
/// 上傳分塊端點另外免除（<c>[DisableRateLimiting]</c>，1 GB 就要 128 塊）。
/// </para>
/// </summary>
public class RateLimitSettings
{
    public const string SectionName = "RateLimitSettings";

    /// <summary>已登入的外部用戶端（帶 JWT 的請求，依帳號）每個時間窗可以呼叫幾次。預設 120。</summary>
    [Range(1, 100_000_000)]
    public int PermitLimitPerUser { get; set; } = 120;

    /// <summary>
    /// 網頁本身（靠登入 Cookie 的請求，依帳號）每個時間窗可以呼叫幾次（1.1.19）。預設 600。
    /// WASM 頁的每個操作都是好幾支 API（待辦頁開一次約 12 支、新增一筆約 6 支），
    /// 與外部程式共用 120 次會誤擋正常操作，所以分開計算、額度較高。
    /// </summary>
    [Range(1, 100_000_000)]
    public int PermitLimitPerWebUser { get; set; } = 600;

    /// <summary>
    /// 未登入的請求（依來源 IP）每個時間窗可以呼叫幾次。預設 30。
    /// 涵蓋 <c>login</c>／<c>refresh</c>，順帶減緩暴力嘗試密碼；同一個公司 NAT 後面的人共用這份額度，所以不要設太小。
    /// </summary>
    [Range(1, 100_000_000)]
    public int PermitLimitPerAnonymousIp { get; set; } = 30;

    /// <summary>時間窗長度（秒）。預設 60。</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
