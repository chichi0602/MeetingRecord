namespace MeetingRecord.Web.Diagnostics;

/// <summary>AI 日誌分析失敗時給使用者看的訊息（1.1.42，API 用）。</summary>
public static class LogAiFailure
{
    /// <summary>把常見失敗翻成使用者看得懂、知道下一步的話。</summary>
    public static string Describe(Exception error)
    {
        var message = error.Message;
        if (message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase))
        {
            return "送出的日誌量超過模型的內容上限。請縮小時間區間、提高最低等級或減少筆數後再試。";
        }

        return $"AI 分析未完成：{message}";
    }
}
