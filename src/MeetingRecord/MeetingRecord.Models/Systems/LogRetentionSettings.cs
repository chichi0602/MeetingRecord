using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// 系統例外紀錄的保存期限（<c>LogRetentionSettings</c> 區段，0.4.122，移植自 NET10-Blazor-Starter 並只留例外紀錄這一項）。
///
/// 由 <c>ExceptionLogRetentionBackgroundService</c> 每天依這裡的天數清除；設成 <c>0</c> 代表不自動清理。
/// 「系統例外紀錄」頁的「清除 N 天未再發生」按鈕也讀這裡（停用自動清理時退回預設天數）。
/// 日誌檔由 nlog.config 的 maxArchiveDays 管，不在這裡。
/// </summary>
public class LogRetentionSettings : IValidatableObject
{
    public const string SectionName = "LogRetentionSettings";

    public const int DefaultExceptionLogDays = 90;

    /// <summary>例外紀錄「最後發生」超過幾天就刪除（連同堆疊檔）。0＝不自動清理。</summary>
    [Range(0, 36500)]
    public int ExceptionLogDays { get; set; } = DefaultExceptionLogDays;

    /// <summary>頁面手動清除用的天數：停用自動清理時仍需要一個合理的門檻。</summary>
    public int ManualExceptionLogDays => ExceptionLogDays > 0 ? ExceptionLogDays : DefaultExceptionLogDays;

    /// <summary>
    /// 用量帳本（Token 用量）保留天數（1.0.29）。<b>預設 0＝永久保留</b>，不自動清。
    /// 要啟用時至少 <see cref="MinimumAiUsageLogDays"/> 天：「本月 vs 上月同期」與每月用量上限都要讀到上個月初。
    /// </summary>
    [Range(0, 36500)]
    public int AiUsageLogDays { get; set; }

    public const int MinimumAiUsageLogDays = 62;

    public const int DefaultAuditLogDays = 365;

    /// <summary>
    /// 稽核紀錄保留天數（1.0.32）。預設 365：稽核是追究責任的證據，不是雜訊，比例外紀錄留得久。0＝不自動清理。
    /// </summary>
    [Range(0, 36500)]
    public int AuditLogDays { get; set; } = DefaultAuditLogDays;

    /// <summary>稽核紀錄頁「清除 N 天前」用的天數：停用自動清理時退回預設天數。</summary>
    public int ManualAuditLogDays => AuditLogDays > 0 ? AuditLogDays : DefaultAuditLogDays;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AiUsageLogDays is > 0 and < MinimumAiUsageLogDays)
        {
            yield return new ValidationResult(
                $"{SectionName}:AiUsageLogDays 只能是 0（不自動清）或至少 {MinimumAiUsageLogDays} 天，否則會清掉上個月的用量。",
                [nameof(AiUsageLogDays)]);
        }
    }
}
