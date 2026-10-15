using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// AI 用量上限（<c>AiQuotaSettings</c> 區段，1.0.29，移植自 NET10-Blazor-Starter 並只留每月上限）。
///
/// <para>
/// 金額是新台幣整數，<b>0＝不限制</b>。用量＝本月用量帳本的估算金額依每筆當時匯率換算成台幣的加總；
/// 未設定單價的呼叫算 0 元。達上限後擋下新的 AI 呼叫；背景工作（轉錄、會議紀錄生成）在開始時檢查一次，
/// 一旦開始就做完。設定以 <c>IOptionsMonitor</c> 讀取，改設定檔即生效、不必重啟。
/// </para>
/// </summary>
public class AiQuotaSettings
{
    public const string SectionName = "AiQuotaSettings";

    /// <summary>全系統每月上限（新台幣）。0＝不限制。</summary>
    [Range(0, 100_000_000)]
    public int GlobalMonthlyTwd { get; set; }

    /// <summary>每位使用者每月上限（新台幣）。0＝不限制。</summary>
    [Range(0, 100_000_000)]
    public int PerUserMonthlyTwd { get; set; }
}
