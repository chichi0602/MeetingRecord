using System.Text;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Dtos.AiCallLogs;

/// <summary>AI 對話紀錄明細窗的檢視方式。</summary>
public enum AiCallLogViewMode
{
    Conversation,
    Request,
    Response,
}

/// <summary>明細窗裡的一個區塊。</summary>
public sealed record AiCallLogDisplayBlock(string Label, string Content, bool IsAssistant);

/// <summary>
/// AI 對話紀錄明細的顯示規則（1.0.30；1.1.38 從 <c>AiCallLogView</c> 抽出，伺服器匯出 PDF 與 WASM 頁共用）。純函式。
/// </summary>
public static class AiCallLogBlocks
{
    /// <summary>單一區塊顯示的字元上限；超過時改成純文字並截斷（完整內容請下載 JSON）。</summary>
    public const int MaxDisplayCharacters = 200_000;

    /// <summary>依檢視方式組出要顯示的區塊。轉錄沒有訊息，送出內容改顯示檔名、時長與段落。</summary>
    public static IReadOnlyList<AiCallLogDisplayBlock> Build(AiCallLogDetail detail, AiCallLogViewMode mode)
    {
        var content = detail.Content;
        if (content is null)
        {
            return [];
        }

        var blocks = new List<AiCallLogDisplayBlock>();
        if (mode != AiCallLogViewMode.Response)
        {
            if (content.Request.Kind == AiCallLogRequest.TranscriptionKind)
            {
                blocks.Add(new AiCallLogDisplayBlock("送出的音訊", DescribeTranscriptionRequest(content.Request), IsAssistant: false));
            }
            else
            {
                foreach (var message in content.Request.Messages)
                {
                    blocks.Add(new AiCallLogDisplayBlock(message.Role == "system" ? "系統指示" : "送出內容", message.Content, IsAssistant: false));
                }

                if (content.Request.Images.Count > 0)
                {
                    blocks.Add(new AiCallLogDisplayBlock(
                        "附帶圖片",
                        string.Join("\n", content.Request.Images.Select((x, i) => $"{i + 1}. {x.MediaType}，{FileSizeFormatter.Describe(x.Bytes)}（只記類型與大小，未保存圖片）")),
                        IsAssistant: false));
                }
            }
        }

        if (mode != AiCallLogViewMode.Request)
        {
            var label = content.Response.IsPartial ? "回應（未完成，只有已收到的部分）" : "回應";
            blocks.Add(new AiCallLogDisplayBlock(label, string.IsNullOrEmpty(content.Response.Text) ? "（沒有內容）" : content.Response.Text, IsAssistant: true));
        }

        return blocks;
    }

    public static string DescribeTranscriptionRequest(AiCallLogRequest request)
    {
        var builder = new StringBuilder();
        builder.Append($"檔案：{request.FileName}");
        if (request.SegmentIndex is { } index && request.SegmentCount is { } count)
        {
            builder.Append($"（第 {index}/{count} 段）");
        }

        if (request.AudioSeconds is { } seconds)
        {
            builder.Append($"\n時長：約 {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}" + (request.IsAudioDurationEstimated ? "（推估）" : string.Empty));
        }

        builder.Append("\n（音訊本身不保存，只記錄檔名、時長與轉出的文字。）");
        return builder.ToString();
    }

    /// <summary>超過 <see cref="MaxDisplayCharacters"/> 時截斷並加註。</summary>
    public static string Truncate(string text)
        => text.Length <= MaxDisplayCharacters
            ? text
            : text[..MaxDisplayCharacters] + $"\n\n……（內容共 {text.Length:N0} 字，只顯示前 {MaxDisplayCharacters:N0} 字；完整內容請下載 JSON）";

    /// <summary>明細窗與 PDF 的「用量與費用」一行。</summary>
    public static string DescribeUsage(AiCallLogUsage? usage)
    {
        if (usage is null)
        {
            return "用量帳本查不到同一次呼叫（帳本可能已被清除）。";
        }

        var amount = usage.EstimatedCost is { } cost
            ? usage.ExchangeRate is { } rate
                ? $"約 {usage.ConvertedCurrency} {cost * rate:N2}（{usage.Currency} {cost:0.######}）"
                : $"{usage.Currency} {cost:0.######}"
            : "未定價";

        var quantity = usage.AudioSeconds is { } seconds
            ? $"音訊 {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}" + (usage.IsAudioDurationEstimated ? "（推估）" : string.Empty)
            : usage.InputTokens is null
                ? "供應商沒有回報用量"
                : $"輸入 {usage.InputTokens:N0}／輸出 {usage.OutputTokens:N0} tokens"
                  + (usage.CachedInputTokens is > 0 ? $"（其中快取 {usage.CachedInputTokens:N0}）" : string.Empty);

        return $"{quantity}；{amount}";
    }
}
