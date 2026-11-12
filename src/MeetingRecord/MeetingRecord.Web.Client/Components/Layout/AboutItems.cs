using MeetingRecord.Dtos.Systems;

namespace MeetingRecord.Web.Components.Layout;

/// <summary>「關於」對話窗一行一行的內容（1.1.6）：由 <see cref="SystemAboutDto"/> 排成固定的欄位順序。</summary>
public static class AboutItems
{
    public static IReadOnlyList<KeyValuePair<string, string>> From(SystemAboutDto about) =>
    [
        new("系統名稱", about.SystemName),
        new("系統描述", about.SystemDescription),
        new("系統版本", about.SystemVersion),
        new("執行環境", about.EnvironmentName),
        new(".NET 版本", about.FrameworkDescription),
        new("啟動時間", about.StartedAt.ToString("yyyy/MM/dd HH:mm:ss")),
        new("已運作時間", about.Uptime.ToString(@"dd\.hh\:mm\:ss")),
    ];
}
