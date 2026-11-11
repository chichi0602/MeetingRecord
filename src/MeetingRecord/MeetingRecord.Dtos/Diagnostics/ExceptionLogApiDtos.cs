namespace MeetingRecord.Dtos.Diagnostics;

/// <summary>系統例外頁要顯示的設定（1.1.40，<c>GET api/v1/exception-logs/meta</c>）。</summary>
public class ExceptionLogMetaDto
{
    /// <summary>自動清除天數（<c>LogRetentionSettings:ExceptionLogDays</c>），0 表示停用。</summary>
    public int RetentionDays { get; set; }

    /// <summary>「清除 N 天未再發生的紀錄」的 N（<c>LogRetentionSettings:ManualExceptionLogDays</c>）。</summary>
    public int PurgeDays { get; set; }

    /// <summary>複製全部、匯出 CSV 一次最多幾列。</summary>
    public int MaxRows { get; set; }

    /// <summary>AI 例外分析能不能用（沒設定供應商時不能）。</summary>
    public bool AiAvailable { get; set; }

    /// <summary>不能用時的原因（按鈕的提示文字）；能用時是空字串。</summary>
    public string AiUnavailableReason { get; set; } = string.Empty;
}

/// <summary>一列的堆疊全文與 AI 分析的送出字數（1.1.40，<c>GET api/v1/exception-logs/{id}/stack-trace</c>）。</summary>
public class ExceptionLogStackTraceDto
{
    /// <summary>堆疊全文；堆疊檔不存在時是 null。</summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// 按「AI 分析」會送出的字數（不含帳號），給確認窗顯示。送出的內容由伺服器依例外編號自己組，不收前端的值。
    /// </summary>
    public int AiPromptChars { get; set; }
}

/// <summary>一次讀多列的堆疊（1.1.40，「複製全部」用）。</summary>
public class ExceptionLogStackTracesRequestDto
{
    public List<int> Ids { get; set; } = [];
}

/// <summary>AI 分析結果上方的一行說明（例外類型、累計次數、模型、用量、耗時）。</summary>
public class ExceptionAiMetaLineDto
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// AI 例外分析串流的一筆事件（1.1.40，一行一筆 JSON）：<c>delta</c> 帶新的一段文字；
/// <c>done</c> 帶完整結果、模型與說明行；<c>error</c> 帶開始產生之後才發生的錯誤訊息。
/// </summary>
public class ExceptionAiStreamEventDto
{
    public const string Delta = "delta";
    public const string Done = "done";
    public const string Error = "error";

    public string Type { get; set; } = Delta;

    public string? Text { get; set; }

    public string? Content { get; set; }

    public string? Model { get; set; }

    public List<ExceptionAiMetaLineDto>? Meta { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// 匯出 AI 例外分析 PDF（1.1.40，<c>POST api/v1/exception-logs/{id}/ai-analysis/pdf</c>）：
/// 前端帶剛收到的結果與說明行；附錄「實際送出的例外內容」由伺服器依例外編號重組，操作者是 API 呼叫者。
/// </summary>
public class ExceptionAiPdfRequestDto
{
    public string Markdown { get; set; } = string.Empty;

    public List<ExceptionAiMetaLineDto> Meta { get; set; } = [];
}
