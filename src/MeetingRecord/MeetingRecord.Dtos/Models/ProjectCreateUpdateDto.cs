using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 專案資料傳輸物件
/// </summary>
public class ProjectCreateUpdateDto
{
    public ProjectCreateUpdateDto()
    {
    }

    /// <summary>
    /// 專案唯一代碼 (僅更新時使用)
    /// </summary>
    [Required(ErrorMessage = "專案唯一代碼 不可為空白")]
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 專案名稱
    /// </summary>
    [Required(ErrorMessage = "專案名稱 不可為空白")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 專案說明
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 專案開始日期
    /// </summary>
    [Required(ErrorMessage = "開始日期 不可為空白")]
    [JsonPropertyName("startDate")]
    public DateTime StartDate { get; set; }

    /// <summary>
    /// 專案結束日期
    /// </summary>
    [Required(ErrorMessage = "結束日期 不可為空白")]
    [JsonPropertyName("endDate")]
    public DateTime EndDate { get; set; }

    /// <summary>
    /// 專案狀態 (未開始/進行中/已完成/已暫停/已取消)
    /// </summary>
    [Required(ErrorMessage = "狀態 不可為空白")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 優先順序 (低/中/高/緊急)
    /// </summary>
    [Required(ErrorMessage = "優先順序 不可為空白")]
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    /// <summary>
    /// 完成百分比 (0-100)
    /// </summary>
    [Range(0, 100, ErrorMessage = "完成百分比必須在 0-100 之間")]
    [JsonPropertyName("completionPercentage")]
    public int CompletionPercentage { get; set; }

    /// <summary>
    /// 專案擁有者
    /// </summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    /// <summary>
    /// 分類標籤（多值，以換行分隔字串儲存，可空）
    /// </summary>
    [JsonPropertyName("categories")]
    public string? Categories { get; set; }

    /// <summary>
    /// 團隊標籤（多值，以換行分隔字串儲存，可空）
    /// </summary>
    [JsonPropertyName("teams")]
    public string? Teams { get; set; }

    /// <summary>
    /// 建立時間
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// 更新時間
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}
