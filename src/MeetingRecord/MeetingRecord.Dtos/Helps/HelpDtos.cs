namespace MeetingRecord.Dtos.Helps;

/// <summary>使用說明頁的內容（1.1.29，<c>GET api/v1/help/manual</c>）。</summary>
public class HelpManualDto
{
    /// <summary>去掉檔頭的說明 Markdown，保留 <c>&lt;!-- 說明分類：… --&gt;</c> 標記（前端用同一套 <c>HelpManual.Parse</c> 切分類與搜尋）。</summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>AI 助理能不能用（沒設定供應商時不能）。</summary>
    public bool AssistantAvailable { get; set; }

    /// <summary>不能用時的原因（畫面放在輸入框的提示文字）；能用時是空字串。</summary>
    public string AssistantUnavailableReason { get; set; } = string.Empty;
}

/// <summary>對話裡已經回答成功的一輪。</summary>
public class HelpAssistantTurnDto
{
    public string Question { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;
}

/// <summary>
/// 問 AI 助理（1.1.29，<c>POST api/v1/help/assistant/ask</c>）。
/// 要不要包含「管理員」章節由伺服器依呼叫者判斷，不收前端的值。
/// </summary>
public class HelpAssistantAskRequestDto
{
    public string Question { get; set; } = string.Empty;

    /// <summary>同一頁上已經回答成功的輪次（伺服器只取最近幾輪並截斷，見 <c>HelpAssistantPromptBuilder</c>）。</summary>
    public List<HelpAssistantTurnDto>? History { get; set; }

    /// <summary>把同一頁上的連續提問在 AI 對話紀錄裡串成一串：<c>help:</c> 加 32 位小寫 16 進位；沒帶由伺服器產生。</summary>
    public string? ThreadKey { get; set; }
}

/// <summary>
/// AI 助理串流的一筆事件（一行一筆 JSON）：<c>delta</c> 帶新的一段文字；<c>done</c> 帶完整回答與模型；
/// <c>error</c> 帶開始產生之後才發生的錯誤訊息。
/// </summary>
public class HelpAssistantStreamEventDto
{
    public const string Delta = "delta";
    public const string Done = "done";
    public const string Error = "error";

    public string Type { get; set; } = Delta;

    public string? Text { get; set; }

    public string? Answer { get; set; }

    public string? Model { get; set; }

    public string? Message { get; set; }
}
