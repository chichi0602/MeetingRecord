using System;
using System.Collections.Generic;
using System.Text;

namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// 專案搜尋請求參數
/// </summary>
public class ProjectSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 擁有者
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>
    /// 專案狀態
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// 優先順序
    /// </summary>
    public string? Priority { get; set; }

    /// <summary>
    /// 開始日期(起)
    /// </summary>
    public DateTime? StartDateFrom { get; set; }

    /// <summary>
    /// 開始日期(迄)
    /// </summary>
    public DateTime? StartDateTo { get; set; }

    /// <summary>
    /// 完成百分比(最小)
    /// </summary>
    public int? CompletionPercentageMin { get; set; }

    /// <summary>
    /// 完成百分比(最大)
    /// </summary>
    public int? CompletionPercentageMax { get; set; }
}
