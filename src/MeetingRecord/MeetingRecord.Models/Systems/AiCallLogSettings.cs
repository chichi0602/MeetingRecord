using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// AI 對話紀錄的設定（<c>AiCallLogSettings</c> 區段，1.0.29，移植自 NET10-Blazor-Starter）。
/// </summary>
public class AiCallLogSettings
{
    public const string SectionName = "AiCallLogSettings";

    public const int DefaultRetentionDays = 90;

    /// <summary>
    /// 是否記錄送出內容與回應。關掉後不再寫新的對話紀錄（用量帳本照記），既有紀錄仍可查看，保留期限照常清除。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 保留天數。內容含會議逐字稿等敏感資料，所以<b>沒有「永久保留」</b>這個選項。
    /// </summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = DefaultRetentionDays;
}
