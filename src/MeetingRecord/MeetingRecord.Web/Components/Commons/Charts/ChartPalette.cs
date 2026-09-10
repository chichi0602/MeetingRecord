using MeetingRecord.Business.Services.Dashboard;

namespace MeetingRecord.Web.Components.Commons.Charts;

/// <summary>
/// 圖表配色。集中在這裡是為了讓「失敗」在圓餅、長條與數字卡上都是同一個顏色——
/// 各元件各自挑色的話，同一個語意在不同圖表會長得不一樣。
///
/// <para>
/// 色碼取自 <c>wwwroot/app.css</c> 的燕麥奶茶色票。此處無法用 CSS 變數：
/// SVG 的 <c>fill</c> 是逐個切片以行內樣式產生的，得是實際色碼。
/// 色票若有調整，這裡要一起改。
/// </para>
/// </summary>
public static class ChartPalette
{
    /// <summary>語意色，與 app.css 的 --oat-ok／--oat-warn／--oat-err 一致。</summary>
    public const string Success = "#6B7A4A";
    public const string Warning = "#B07D3A";
    public const string Danger = "#A6483C";

    /// <summary>一般項目依序取用的色階（深→淺），來自 --oat-ink 到 --oat-200。</summary>
    private static readonly string[] NeutralRamp =
    [
        "#43362C",
        "#7A6A5A",
        "#C2A284",
        "#C7AA8F",
        "#D4BDA8",
        "#DBC8B7",
    ];

    /// <summary>
    /// 取得某一個切片的顏色。
    /// 有語意的（成功／進行中／失敗）固定用語意色；其餘依索引輪流取色階。
    /// </summary>
    public static string Resolve(ChartTone tone, int neutralIndex) => tone switch
    {
        ChartTone.Success => Success,
        ChartTone.Warning => Warning,
        ChartTone.Danger => Danger,
        _ => NeutralRamp[neutralIndex % NeutralRamp.Length],
    };
}
