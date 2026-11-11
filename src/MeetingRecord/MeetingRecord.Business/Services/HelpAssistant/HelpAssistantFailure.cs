using MeetingRecord.Business.Services.AiUsage;

namespace MeetingRecord.Business.Services.HelpAssistant;

/// <summary>AI 助理沒有回答成功時給使用者看的訊息（1.1.29 從 <c>HelpView</c> 抽出，Server 頁與 API 共用；文字不變）。</summary>
public static class HelpAssistantFailure
{
    public static string Describe(Exception ex)
    {
        if (ex is AiQuotaExceededException)
        {
            return ex.Message;
        }

        var message = ex.Message;
        if (message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase))
        {
            return "使用說明超過目前 AI 模型能讀的長度，請聯絡系統管理員。";
        }

        return $"AI 助理沒有回答成功：{message}";
    }
}
