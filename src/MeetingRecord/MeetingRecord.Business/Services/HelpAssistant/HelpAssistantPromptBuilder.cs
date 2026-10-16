using System.Text;

namespace MeetingRecord.Business.Services.HelpAssistant;

/// <summary>對話裡的一輪（使用者問、AI 答）。</summary>
public sealed record HelpAssistantTurn(string Question, string Answer);

/// <summary>
/// 使用說明 AI 助理的提示詞（1.0.43）。純函式，方便單元測試。
/// </summary>
public static class HelpAssistantPromptBuilder
{
    /// <summary>只帶最近幾輪：每一輪都會跟著整份說明一起再送一次，帶太多會越問越貴。</summary>
    public const int MaxHistoryTurns = 3;

    /// <summary>歷史裡每則問題／答案的上限字數。</summary>
    public const int MaxHistoryMessageLength = 2000;

    /// <summary>本次問題的上限字數。操作問題通常一兩句，貼一大段進來多半是誤貼。</summary>
    public const int MaxQuestionLength = 1000;

    /// <summary>
    /// ⚠️ 「只根據使用說明回答」是這個功能的核心：說明沒寫的功能 AI 很會自己編，
    /// 編出來的按鈕名稱與步驟使用者照著找會找不到，比沒回答更糟。
    /// </summary>
    public const string SystemPrompt =
        "你是「AI 會議記錄小助手」這套系統的操作助理，負責回答使用者在操作上的問題。\n"
        + "規則：\n"
        + "1. 只根據 <使用說明> 裡的內容回答，不要憑印象補充說明裡沒有的功能、按鈕或步驟。\n"
        + "2. 使用說明沒有寫到的，直接回答「使用說明沒有寫到這件事」，並建議聯絡系統管理員；不要猜。\n"
        + "3. 全程使用繁體中文（台灣用語），不要使用簡體字。\n"
        + "4. 有操作步驟時用條列，按鈕與頁面名稱照說明裡的寫法用「」標出。回答盡量簡短，先講結論。\n"
        + "5. 回答最後另起一行註明參考的章節，格式：「參考：章節名稱 › 小節名稱」。\n"
        + "6. 只回答這套系統的操作問題；與系統無關的問題，禮貌說明你只能回答操作上的問題。";

    /// <summary>組出 user prompt：使用說明全文、最近幾輪對話、本次問題。</summary>
    /// <exception cref="ArgumentException">問題是空白。</exception>
    public static string BuildUserPrompt(string manual, IReadOnlyList<HelpAssistantTurn> history, string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var builder = new StringBuilder();
        builder.AppendLine("<使用說明>");
        builder.AppendLine((manual ?? string.Empty).Trim());
        builder.AppendLine("</使用說明>");

        var recent = (history ?? []).TakeLast(MaxHistoryTurns).ToList();
        if (recent.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("<先前的對話>");
            foreach (var turn in recent)
            {
                builder.AppendLine($"使用者：{Truncate(turn.Question, MaxHistoryMessageLength)}");
                builder.AppendLine($"助理：{Truncate(turn.Answer, MaxHistoryMessageLength)}");
            }

            builder.AppendLine("</先前的對話>");
        }

        builder.AppendLine();
        builder.AppendLine($"使用者的問題：{Truncate(question.Trim(), MaxQuestionLength)}");
        return builder.ToString();
    }

    /// <summary>AI 對話紀錄清單上的關聯說明：問題的開頭。</summary>
    public static string BuildRelatedInfo(string question)
        => $"使用說明提問：{Truncate(question.Trim().ReplaceLineEndings(" "), 40)}";

    private static string Truncate(string? text, int maxLength)
    {
        var value = text ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "…（以下省略）";
    }
}
