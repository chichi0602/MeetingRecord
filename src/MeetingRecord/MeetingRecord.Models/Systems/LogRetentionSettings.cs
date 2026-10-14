using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// 系統例外紀錄的保存期限（<c>LogRetentionSettings</c> 區段，0.4.122，移植自 NET10-Blazor-Starter 並只留例外紀錄這一項）。
///
/// 由 <c>ExceptionLogRetentionBackgroundService</c> 每天依這裡的天數清除；設成 <c>0</c> 代表不自動清理。
/// 「系統例外紀錄」頁的「清除 N 天未再發生」按鈕也讀這裡（停用自動清理時退回預設天數）。
/// 日誌檔由 nlog.config 的 maxArchiveDays 管，不在這裡。
/// </summary>
public class LogRetentionSettings
{
    public const string SectionName = "LogRetentionSettings";

    public const int DefaultExceptionLogDays = 90;

    /// <summary>例外紀錄「最後發生」超過幾天就刪除（連同堆疊檔）。0＝不自動清理。</summary>
    [Range(0, 36500)]
    public int ExceptionLogDays { get; set; } = DefaultExceptionLogDays;

    /// <summary>頁面手動清除用的天數：停用自動清理時仍需要一個合理的門檻。</summary>
    public int ManualExceptionLogDays => ExceptionLogDays > 0 ? ExceptionLogDays : DefaultExceptionLogDays;
}
