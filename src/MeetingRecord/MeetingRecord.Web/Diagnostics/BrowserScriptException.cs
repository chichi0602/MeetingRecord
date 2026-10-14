namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 代表一個瀏覽器端的 JavaScript 錯誤（0.4.122）。
///
/// 系統例外紀錄只收「Error 以上且帶例外物件」的日誌，所以前端錯誤要包成例外才進得去。
/// 型別名稱固定，簽章由「訊息＋頁面＋操作」區分：同一個 JS 錯誤在同一頁重複發生會合併計次。
/// <see cref="ToString"/> 帶出 JS 的堆疊，堆疊檔看到的是瀏覽器端的呼叫路徑，而不是伺服器接收端。
/// </summary>
public sealed class BrowserScriptException : Exception
{
    public BrowserScriptException(string kind, string message, string? scriptStack, string? source)
        : base(message)
    {
        Kind = kind;
        ScriptStack = scriptStack ?? string.Empty;
        Source = source;
    }

    /// <summary><c>error</c> 或 <c>unhandledrejection</c>。</summary>
    public string Kind { get; }

    public string ScriptStack { get; }

    public override string StackTrace => ScriptStack;

    public override string ToString()
        => $"{GetType().FullName}: {Message}{Environment.NewLine}Kind: {Kind}{Environment.NewLine}"
            + (string.IsNullOrWhiteSpace(Source) ? string.Empty : $"Source: {Source}{Environment.NewLine}")
            + ScriptStack;
}
