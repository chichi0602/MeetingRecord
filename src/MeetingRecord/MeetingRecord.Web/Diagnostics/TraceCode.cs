using System.Security.Cryptography;
using NLog;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 錯誤追蹤碼（0.4.122，移植自 NET10-Blazor-Starter）：使用者在錯誤訊息上看到的 8 碼短碼，管理員用它在 /logs 與系統例外紀錄找到同一次操作。
///
/// 一個碼代表「一次 HTTP 請求」或「一次 Blazor 互動」：
/// - HTTP：<c>UseHttpRequestLogging</c> 產生並<b>取代 <c>HttpContext.TraceIdentifier</c></b>，
///   於是 API 回應的 TraceId 與框架自己的日誌都是同一個碼（/Error 頁仍顯示 Activity Id，不是追蹤碼）。
/// - Blazor：<c>ApplicationCircuitHandler.CreateInboundActivityHandler</c> 每次互動產生一個
///   （circuit 內沒有 HttpContext，TraceIdentifier 是空的或過期的）。
/// 兩者都放進 NLog 的 ScopeContext，nlog.config 的 TraceId 欄位以 <c>${scopeproperty:item=TraceCode}</c> 輸出。
///
/// 只含隨機字元（Crockford Base32，去掉易混淆的 I／L／O／U），不帶時間、主機或帳號資訊。
/// </summary>
public static class TraceCode
{
    public const string ScopePropertyName = "TraceCode";

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 8;

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[Length];
        RandomNumberGenerator.Fill(bytes);

        Span<char> chars = stackalloc char[Length];
        for (var index = 0; index < Length; index++)
        {
            chars[index] = Alphabet[bytes[index] % Alphabet.Length];
        }

        return new string(chars);
    }

    /// <summary>目前這次請求／互動的追蹤碼；不在任何請求或互動之內時為 null。</summary>
    public static string? Current
        => ScopeContext.TryGetProperty(ScopePropertyName, out var value) ? value as string : null;

    /// <summary>開始一段追蹤範圍。用法：<c>using var _ = TraceCode.Begin(code);</c></summary>
    public static IDisposable Begin(string code) => ScopeContext.PushProperty(ScopePropertyName, code);

    /// <summary>附在錯誤訊息後面的文字；沒有追蹤碼時回傳空字串。</summary>
    public static string Suffix(string? code)
        => string.IsNullOrEmpty(code) ? string.Empty : $"（錯誤追蹤碼：{code}）";

    private const string ExceptionDataKey = "MeetingRecord.TraceCode";

    /// <summary>
    /// 把當下的追蹤碼記在例外上，讓錯誤邊界的 ErrorContent（拿得到例外、拿不到當時的範圍）顯示得出來。
    /// </summary>
    public static void Attach(Exception exception)
    {
        var code = Current;
        if (code is null)
        {
            return;
        }

        try
        {
            exception.Data[ExceptionDataKey] = code;
        }
        catch (Exception ex)
        {
            // 少數例外型別的 Data 是唯讀的；只是少了畫面上的追蹤碼，日誌仍有。
            NLog.Common.InternalLogger.Debug(ex, "Failed to attach trace code to exception.");
        }
    }

    public static string? FromException(Exception? exception)
        => exception?.Data.Contains(ExceptionDataKey) == true ? exception.Data[ExceptionDataKey] as string : null;
}
