using System.Globalization;

namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 圖表的純幾何計算（1.1.12 從 Business 的 <c>DashboardMetrics</c> 搬來）：圓餅圖元件已搬到 WASM 專案，
/// 伺服器端（<c>DashboardMetrics.BuildArcPath</c> 轉呼叫這裡）與瀏覽器共用同一份。
/// </summary>
public static class ChartGeometry
{
    /// <summary>
    /// 產生圓餅（甜甜圈）單一切片的 SVG path。
    ///
    /// <para>
    /// <paramref name="sweepDegrees"/> 達到 360 度時**必須特判**：SVG 的 arc 在起點與終點
    /// 重合時畫不出東西（等於要求「從 A 畫弧到 A」，繪圖引擎會直接略過），
    /// 整個圓餅只有一個切片時就會變成空白。這裡改用兩段半圓接成整圓。
    /// </para>
    /// </summary>
    public static string BuildArcPath(
        double centerX,
        double centerY,
        double radius,
        double startDegrees,
        double sweepDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        // 用 >= 359.999 而不是 == 360：角度是累加出來的浮點數，不會剛好等於 360。
        if (sweepDegrees >= 359.999)
        {
            var top = FormatPoint(centerX, centerY - radius);
            var bottom = FormatPoint(centerX, centerY + radius);
            var r = Format(radius);

            return $"M {top} A {r} {r} 0 1 1 {bottom} A {r} {r} 0 1 1 {top}";
        }

        var start = PointOnCircle(centerX, centerY, radius, startDegrees);
        var end = PointOnCircle(centerX, centerY, radius, startDegrees + sweepDegrees);
        var largeArc = sweepDegrees > 180 ? 1 : 0;

        return $"M {FormatPoint(centerX, centerY)} L {start} A {Format(radius)} {Format(radius)} 0 {largeArc} 1 {end} Z";
    }

    private static string PointOnCircle(double centerX, double centerY, double radius, double degrees)
    {
        // -90 度是為了讓 0 度落在正上方（一般人讀圓餅圖的起點），而不是數學慣例的正右方。
        var radians = (degrees - 90) * Math.PI / 180;

        return FormatPoint(
            centerX + (radius * Math.Cos(radians)),
            centerY + (radius * Math.Sin(radians)));
    }

    private static string FormatPoint(double x, double y) => $"{Format(x)} {Format(y)}";

    /// <summary>SVG 的數值一律用不變文化格式——遇到以逗號當小數點的地區會產生無效路徑。</summary>
    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
