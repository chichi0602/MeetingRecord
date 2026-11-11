using Microsoft.JSInterop;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Web.Client.Api;

namespace MeetingRecord.Web.Client.Services;

/// <summary>
/// 瀏覽器錯誤的 WASM 端回報器（1.1.47）：<c>wwwroot/js/client-error-reporter.js</c> 捕捉 <c>error</c>／<c>unhandledrejection</c> 後
/// 呼叫 <see cref="Report"/>，這裡轉送到 <c>POST api/v1/client-errors</c>。由 <c>WasmLayout</c> 在登入狀態確認後註冊，
/// 所以只有登入者的頁面會回報；註冊前發生的錯誤由 JS 暫存（上限 20 筆），註冊時一次送出。
///
/// <para>回報本身失敗（網路斷了、伺服器拒絕）一律吞掉，不可再丟出錯誤，否則會自己觸發自己。</para>
/// </summary>
public sealed class ClientErrorReporter(ApiClient api)
{
    private const string Url = "api/v1/client-errors";

    [JSInvokable]
    public async Task Report(string? kind, string? message, string? stack, string? source, string? path)
    {
        try
        {
            await api.PostAsync(
                Url,
                new ClientErrorReportDto { Kind = kind, Message = message, Stack = stack, Source = source, Path = path },
                ClientJsonContext.Default.ClientErrorReportDto,
                ClientJsonContext.Default.BoolResult);
        }
        catch
        {
            // 見類別說明：回報失敗不再往外丟。
        }
    }
}
