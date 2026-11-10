using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// WASM 端呼叫 Web API 的基底（1.1.1）。之後每個功能的 <c>XxxApiClient</c> 都經過這裡：
/// <list type="bullet">
/// <item>一律解成 <see cref="ApiResult{T}"/>；回應不是 ApiResult（例如代理伺服器的錯誤頁）也回失敗結果，不丟例外。</item>
/// <item>401（未登入、登入失效）整頁導到登入頁。</item>
/// <item>403 且原因是須改密碼（1.1.4，伺服器的 MustChangePasswordFilter）整頁導到變更密碼頁。</item>
/// </list>
/// </summary>
public sealed class ApiClient(HttpClient http, NavigationManager navigation)
{
    public const string LoginPath = "/Auths/Login";
    public const string ChangePasswordPath = "/ChangePassword";
    public const string MustChangePasswordReason = "must-change-password";

    public async Task<ApiResult<T>> GetAsync<T>(string url, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    /// <summary>不帶內容的 POST（例如「全部標為已讀」）。</summary>
    public async Task<ApiResult<T>> PostAsync<T>(string url, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(url, content: null, cancellationToken);
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    /// <summary>帶 JSON 內容的 POST；內容與回應都走原始碼產生器（<see cref="ClientJsonContext"/>）。</summary>
    public async Task<ApiResult<T>> PostAsync<TBody, T>(string url, TBody body, JsonTypeInfo<TBody> bodyType, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(url, body, bodyType, cancellationToken);
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    /// <summary>帶 JSON 內容的 PUT（1.1.7，修改、切換啟用）。</summary>
    public async Task<ApiResult<T>> PutAsync<TBody, T>(string url, TBody body, JsonTypeInfo<TBody> bodyType, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync(url, body, bodyType, cancellationToken);
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    /// <summary>DELETE（1.1.7）。</summary>
    public async Task<ApiResult<T>> DeleteAsync<T>(string url, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync(url, cancellationToken);
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    private async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            navigation.NavigateTo(LoginPath, forceLoad: true, replace: true);
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
            && reasons.Contains(MustChangePasswordReason))
        {
            navigation.NavigateTo(ChangePasswordPath, forceLoad: true, replace: true);
        }

        return result ?? ApiResult<T>.FailureResult($"伺服器回應無法解讀（HTTP {(int)response.StatusCode}）。", (int)response.StatusCode);
    }
}
