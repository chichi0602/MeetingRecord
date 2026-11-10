using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 會議紀錄提示詞資料傳輸物件
/// </summary>
public class PromptTemplateCreateUpdateDto
{
    public PromptTemplateCreateUpdateDto()
    {
    }

    /// <summary>
    /// 提示詞唯一代碼 (僅更新時使用)
    /// </summary>
    [Required(ErrorMessage = "提示詞唯一代碼 不可為空白")]
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 提示詞名稱
    /// </summary>
    [Required(ErrorMessage = "提示詞名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 提示詞內容（可含 {{transcript}} 等變數佔位符）
    /// </summary>
    [Required(ErrorMessage = "提示詞內容 不可為空白")]
    [StringLength(20000, ErrorMessage = "提示詞內容長度不可超過 20000 字元")]
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 提示詞描述
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 是否啟用
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 分類標籤（多值，以分隔字串儲存，可空）
    /// </summary>
    [JsonPropertyName("categories")]
    public string? Categories { get; set; }

    /// <summary>
    /// 分類名稱清單（1.1.9）。有帶就以這個為準；沒帶才看 <see cref="Categories"/>（資料庫的儲存格式：每個名稱前後各一個換行，例如 "\n分類A\n分類B\n"）。
    /// 兩個都沒帶＝沒有分類。回應兩個都會填。
    /// </summary>
    [JsonPropertyName("categoryNames")]
    public List<string>? CategoryNames { get; set; }

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
