namespace MeetingRecord.Dtos.Diagnostics;

/// <summary>AI 分析結果上方的一行說明（例外類型、查詢區間、模型、用量、耗時等）。AI 例外分析（1.1.40）與 AI 日誌分析（1.1.42）共用。</summary>
public class DiagnosticAiMetaLineDto
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// 監控與診斷 AI 分析串流的一筆事件（1.1.40 例外分析起；1.1.42 日誌分析共用，一行一筆 JSON）：
/// <c>delta</c> 帶新的一段文字；<c>done</c> 帶完整結果、模型與說明行；<c>error</c> 帶開始產生之後才發生的錯誤訊息。
/// </summary>
public class DiagnosticAiStreamEventDto
{
    public const string Delta = "delta";
    public const string Done = "done";
    public const string Error = "error";

    public string Type { get; set; } = Delta;

    public string? Text { get; set; }

    public string? Content { get; set; }

    public string? Model { get; set; }

    public List<DiagnosticAiMetaLineDto>? Meta { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// 匯出 AI 例外分析 PDF（1.1.40，<c>POST api/v1/exception-logs/{id}/ai-analysis/pdf</c>）：
/// 前端帶剛收到的結果與說明行；附錄「實際送出的例外內容」由伺服器依例外編號重組，操作者是 API 呼叫者。
/// </summary>
public class DiagnosticAiPdfRequestDto
{
    public string Markdown { get; set; } = string.Empty;

    public List<DiagnosticAiMetaLineDto> Meta { get; set; } = [];
}

/// <summary>日誌檢視頁要顯示的設定（1.1.42，<c>GET api/v1/monitoring/logs/meta</c>）：AI 日誌分析能不能用。</summary>
public class LogViewerMetaDto
{
    public bool AiAvailable { get; set; }

    /// <summary>不能用時的原因（按鈕的提示文字）；能用時是空字串。</summary>
    public string AiUnavailableReason { get; set; } = string.Empty;
}

/// <summary>
/// 匯出 AI 日誌分析 PDF（1.1.42，<c>POST api/v1/monitoring/logs/ai-analysis/pdf</c>）：
/// 前端帶剛收到的結果與說明行，以及當時的查詢條件；附錄「實際送出的日誌」由伺服器依同一組條件重查重組，操作者是 API 呼叫者。
/// </summary>
public class LogAiPdfRequestDto
{
    public LogQueryRequest Query { get; set; } = new();

    public string Markdown { get; set; } = string.Empty;

    public List<DiagnosticAiMetaLineDto> Meta { get; set; } = [];
}
