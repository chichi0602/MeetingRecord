namespace MeetingRecord.Dtos.Diagnostics;

/// <summary>
/// 瀏覽器端錯誤（1.1.47，<c>POST api/v1/client-errors</c>）：<c>client-error-reporter.js</c> 捕捉的 <c>error</c>／<c>unhandledrejection</c>。
/// 前端先截過長度，伺服器還會再截一次、頁面只留路徑（不信任前端）。
/// </summary>
public class ClientErrorReportDto
{
    /// <summary><c>error</c> 或 <c>unhandledrejection</c>；其他值一律當 <c>error</c>。</summary>
    public string? Kind { get; set; }

    public string? Message { get; set; }

    public string? Stack { get; set; }

    /// <summary>出錯的腳本位置（檔案:行:欄）。</summary>
    public string? Source { get; set; }

    /// <summary>出錯時的頁面路徑；伺服器只留路徑（去掉查詢字串與錨點，可能含個資或權杖）。</summary>
    public string? Path { get; set; }
}
