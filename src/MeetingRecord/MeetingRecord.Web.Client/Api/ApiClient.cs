using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>下載的結果（1.1.22）。</summary>
public sealed record ApiDownload(bool Success, byte[]? Content, string? FileName, string? ContentType, string? Error, int StatusCode);

/// <summary>
/// WASM 端呼叫 Web API 的基底（1.1.1）。之後每個功能的 <c>XxxApiClient</c> 都經過這裡：
/// <list type="bullet">
/// <item>一律解成 <see cref="ApiResult{T}"/>；回應不是 ApiResult（例如代理伺服器的錯誤頁）也回失敗結果，不丟例外。</item>
/// <item>連不上伺服器（斷網）也回失敗結果（1.1.23，狀態碼 <see cref="NetworkErrorStatusCode"/>）：以前會丟例外，整頁被錯誤邊界換成錯誤畫面。</item>
/// <item>401（未登入、登入失效）整頁導到登入頁，同時好幾個 401 也只導一次（1.1.54）。</item>
/// <item>403 且原因是須改密碼（1.1.4，伺服器的 MustChangePasswordFilter）整頁導到變更密碼頁。</item>
/// </list>
/// </summary>
public sealed class ApiClient(HttpClient http, NavigationManager navigation)
{
    public const string LoginPath = "/Auths/Login";
    public const string ChangePasswordPath = "/ChangePassword";
    public const string MustChangePasswordReason = "must-change-password";

    /// <summary>連不上伺服器時失敗結果的狀態碼（沒有 HTTP 回應）。</summary>
    public const int NetworkErrorStatusCode = 0;

    public const string NetworkErrorMessage = "無法連線到伺服器，請確認網路後再試一次。";

    /// <summary>
    /// 401 整頁導到登入頁之前先呼叫（1.1.23）：上傳中開著「確定要離開嗎？」（<c>beforeunload</c>），不先拿掉的話瀏覽器會擋下這次導向。
    /// </summary>
    public event Func<Task>? BeforeLoginRedirect;

    /// <summary>已經開始導向登入頁（WASM 只有一條執行緒，不必鎖）。</summary>
    private bool redirectingToLogin;

    public Task<ApiResult<T>> GetAsync<T>(string url, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
        => SendAsync(() => http.GetAsync(url, cancellationToken), typeInfo, cancellationToken);

    /// <summary>不帶內容的 POST（例如「全部標為已讀」）。</summary>
    public Task<ApiResult<T>> PostAsync<T>(string url, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
        => SendAsync(() => http.PostAsync(url, content: null, cancellationToken), typeInfo, cancellationToken);

    /// <summary>帶 JSON 內容的 POST；內容與回應都走原始碼產生器（<see cref="ClientJsonContext"/>）。</summary>
    public Task<ApiResult<T>> PostAsync<TBody, T>(string url, TBody body, JsonTypeInfo<TBody> bodyType, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
        => SendAsync(() => http.PostAsJsonAsync(url, body, bodyType, cancellationToken), typeInfo, cancellationToken);

    /// <summary>帶 JSON 內容的 PUT（1.1.7，修改、切換啟用）。</summary>
    public Task<ApiResult<T>> PutAsync<TBody, T>(string url, TBody body, JsonTypeInfo<TBody> bodyType, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
        => SendAsync(() => http.PutAsJsonAsync(url, body, bodyType, cancellationToken), typeInfo, cancellationToken);

    /// <summary>DELETE（1.1.7）。</summary>
    public Task<ApiResult<T>> DeleteAsync<T>(string url, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
        => SendAsync(() => http.DeleteAsync(url, cancellationToken), typeInfo, cancellationToken);

    /// <summary>
    /// 下載檔案（1.1.22）：成功回內容與檔名；失敗回原因（401、須改密碼照樣導向）。
    /// </summary>
    public async Task<ApiDownload> DownloadAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage sent;
        try
        {
            sent = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new ApiDownload(false, null, null, null, NetworkErrorMessage, NetworkErrorStatusCode);
        }

        using var response = sent;
        if (response.IsSuccessStatusCode)
        {
            var headers = response.Content.Headers;
            var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? "download";
            return new ApiDownload(true, await response.Content.ReadAsByteArrayAsync(cancellationToken), fileName, headers.ContentType?.MediaType ?? "application/octet-stream", null, (int)response.StatusCode);
        }

        var failure = await ReadAsync(response, ClientJsonContext.Default.EmptyResult, cancellationToken);
        return new ApiDownload(false, null, null, null, failure.ErrorMessage ?? failure.Message, failure.StatusCode);
    }

    /// <summary>
    /// 送出串流請求（1.1.22，AI 問答）：讓瀏覽器邊收邊交給 .NET（<c>SetBrowserResponseStreamingEnabled</c>），
    /// 只等到回應標頭就回來。呼叫端要自己釋放回應；失敗的回應用 <see cref="ReadFailureAsync"/> 讀原因。
    /// </summary>
    public Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        Microsoft.AspNetCore.Components.WebAssembly.Http.WebAssemblyHttpRequestMessageExtensions.SetBrowserResponseStreamingEnabled(request, true);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>讀失敗回應的原因（401、須改密碼照樣導向）。</summary>
    public Task<ApiResult<object>> ReadFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
        => ReadAsync(response, ClientJsonContext.Default.EmptyResult, cancellationToken);

    /// <summary>
    /// 失敗原因（1.1.23 從 TodoApi 搬來，會議頁也用）：既有對外 API 的新增、修改、刪除把原因放在 <c>errors.Validation</c>，
    /// 新端點放 <c>errorMessage</c>，兩種都認。
    /// </summary>
    public static string ErrorText<T>(ApiResult<T> result)
        => result.ErrorMessage
           ?? result.Errors?.Values.SelectMany(x => x).FirstOrDefault()
           ?? result.Message;

    /// <summary>變更密碼頁本身（1.1.13 起在 WASM）：在這頁收到「須改密碼」的 403 不再導向自己，免得重新載入不停。</summary>
    /// <summary>送出並解讀回應；連不上伺服器（斷網、伺服器沒開）時回失敗結果，不丟例外。</summary>
    private async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken)
    {
        HttpResponseMessage sent;
        try
        {
            sent = await send();
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.FailureResult(NetworkErrorMessage, NetworkErrorStatusCode);
        }

        using var response = sent;
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    private bool IsOnChangePasswordPage()
        => string.Equals("/" + navigation.ToBaseRelativePath(navigation.Uri).Split('?', '#')[0], ChangePasswordPath, StringComparison.OrdinalIgnoreCase);

    private async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // 只導一次（1.1.54）：登入一失效，同時進行中的請求會各自拿到 401，每個都整頁導向的話後一次會打斷前一次。
            // 整頁載入後 ApiClient 重新建立，旗標自然歸零。
            if (!redirectingToLogin)
            {
                redirectingToLogin = true;
                foreach (var handler in BeforeLoginRedirect?.GetInvocationList().Cast<Func<Task>>() ?? [])
                {
                    await handler();
                }

                navigation.NavigateTo(LoginPath, forceLoad: true, replace: true);
            }

            return ApiResult<T>.UnauthorizedResult("尚未登入或登入已失效。");
        }

        ApiResult<T>? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
        }
        catch (JsonException)
        {
        }

        if (response.StatusCode == HttpStatusCode.Forbidden
            && result?.Errors?.TryGetValue("Reason", out var reasons) == true
            && reasons.Contains(MustChangePasswordReason)
            && !IsOnChangePasswordPage())
        {
            navigation.NavigateTo(ChangePasswordPath, forceLoad: true, replace: true);
        }

        return result ?? ApiResult<T>.FailureResult($"伺服器回應無法解讀（HTTP {(int)response.StatusCode}）。", (int)response.StatusCode);
    }
}
