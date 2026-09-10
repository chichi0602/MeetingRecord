using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Business.Services.TodoExtraction;

/// <summary>AI 從會議紀錄抽出來的一條待辦候選（尚未落庫，使用者可在確認視窗改）。</summary>
public sealed record ExtractedTodo(
    string Title,
    string? Description,
    string? Owner,
    DateTime? DueDate,
    string Priority);

/// <summary>
/// 把 LLM 回傳的文字解析成待辦候選清單。
///
/// <para>
/// **這是整個功能唯一真正脆弱的地方。** 模型不保證回乾淨的 JSON——它可能包上
/// ```json 圍籬、前後加客套話、日期寫成「下週五」、優先度寫成「High」。
/// 這裡全部吸收掉，讓上層永遠拿到一份乾淨、可直接進 <see cref="TodoAdapterModel"/> 的清單。
/// </para>
///
/// <para>
/// 抽成 static 純函式是為了能單元測試（本專案既有慣例，見 <c>AiChatService.BuildUserPrompt</c>）——
/// 這種「處理模型各種歪掉的輸出」的邏輯不寫測試根本無從驗證。
/// </para>
/// </summary>
public static class TodoExtractionParser
{
    /// <summary>標題長度上限，與 <see cref="TodoAdapterModel.Title"/> 的 StringLength 一致。</summary>
    private const int TitleMaxLength = 200;

    /// <summary>描述長度上限，與 <see cref="TodoAdapterModel.Description"/> 的 StringLength 一致。</summary>
    private const int DescriptionMaxLength = 2000;

    /// <summary>負責人長度上限，與 <see cref="TodoAdapterModel.Owner"/> 的 StringLength 一致。</summary>
    private const int OwnerMaxLength = 50;

    /// <summary>模型可能用的日期格式。只收絕對日期——相對日期一律當作沒填。</summary>
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "yyyy.MM.dd",
        "yyyyMMdd",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>模型輸出的原始形狀。全部可空——缺欄位是常態，不是例外。</summary>
    private sealed record RawTodo(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("owner")] string? Owner,
        [property: JsonPropertyName("dueDate")] string? DueDate,
        [property: JsonPropertyName("priority")] string? Priority);

    /// <summary>
    /// 解析模型輸出。**任何解析不出來的情況都回空清單，不拋例外**——
    /// 「這份會議紀錄看不出待辦」與「模型今天回了奇怪的東西」對使用者來說
    /// 是同一件事：畫面上顯示提示，而不是一個紅色錯誤。
    /// </summary>
    public static IReadOnlyList<ExtractedTodo> Parse(string? raw)
    {
        var json = ExtractJsonArray(raw);
        if (json is null)
        {
            return [];
        }

        RawTodo?[]? items;
        try
        {
            items = JsonSerializer.Deserialize<RawTodo?[]>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        if (items is null)
        {
            return [];
        }

        var results = new List<ExtractedTodo>(items.Length);

        foreach (var item in items)
        {
            // 沒有標題的候選整條丟掉——待辦標題是必填，留下來只會在寫入時才失敗。
            var title = Truncate(item?.Title?.Trim(), TitleMaxLength);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            results.Add(new ExtractedTodo(
                title,
                Truncate(NullIfBlank(item?.Description), DescriptionMaxLength),
                Truncate(NullIfBlank(item?.Owner), OwnerMaxLength),
                ParseDueDate(item?.DueDate),
                NormalizePriority(item?.Priority)));
        }

        return results;
    }

    /// <summary>
    /// 從模型輸出裡挖出 JSON 陣列。
    ///
    /// 取第一個 <c>[</c> 到最後一個 <c>]</c>，這同時處理掉三種常見情況：
    /// ```json 圍籬、前面的「以下是我抽出的待辦：」、後面的補充說明。
    /// </summary>
    private static string? ExtractJsonArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');

        return start >= 0 && end > start
            ? raw[start..(end + 1)]
            : null;
    }

    /// <summary>
    /// 解析截止日。**只收絕對日期**——「下週五」「三天內」一律當作沒填。
    ///
    /// 刻意不去推算相對日期：算錯一個截止日比留白更糟，留白使用者一眼看得出要補，
    /// 算錯了他不會發現。提示詞那端也已經要求模型只輸出 yyyy-MM-dd。
    /// </summary>
    private static DateTime? ParseDueDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        return DateTime.TryParseExact(
            text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Date
            : null;
    }

    /// <summary>
    /// 正規化優先度到 <see cref="TodoAdapterModel.PriorityOptions"/>。
    /// 不認得的一律給「中」——寧可全部落在中間讓使用者自己調，也不要亂猜成「高」。
    /// </summary>
    private static string NormalizePriority(string? value)
    {
        var fallback = TodoAdapterModel.PriorityOptions[1];

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.Trim();

        foreach (var option in TodoAdapterModel.PriorityOptions)
        {
            if (string.Equals(text, option, StringComparison.Ordinal))
            {
                return option;
            }
        }

        // 模型偶爾會回英文，這三個對應是穩定的。其餘（「緊急」「一般」…）一律落到預設值。
        return text.ToLowerInvariant() switch
        {
            "high" => TodoAdapterModel.PriorityOptions[2],
            "medium" => TodoAdapterModel.PriorityOptions[1],
            "low" => TodoAdapterModel.PriorityOptions[0],
            _ => fallback,
        };
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength)
        => value is not null && value.Length > maxLength ? value[..maxLength] : value;
}
