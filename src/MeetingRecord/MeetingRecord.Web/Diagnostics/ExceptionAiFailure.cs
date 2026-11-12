namespace MeetingRecord.Web.Diagnostics;

/// <summary>AI 例外分析失敗時給使用者看的訊息（1.1.40，API 用）。</summary>
public static class ExceptionAiFailure
{
    public static string Describe(Exception error)
        => error.Message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            ? "這筆例外的內容（多半是堆疊）超過模型的內容上限，無法送交 AI 分析。"
            : $"AI 分析未完成：{error.Message}";
}
