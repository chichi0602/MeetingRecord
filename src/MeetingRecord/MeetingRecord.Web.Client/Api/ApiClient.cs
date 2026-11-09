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
/// </list>
/// </summary>
public sealed class ApiClient(HttpClient http, NavigationManager navigation)
{
    public const string LoginPath = "/Auths/Login";

    public async Task<ApiResult<T>> GetAsync<T>(string url, JsonTypeInfo<ApiResult<T>> typeInfo, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(url, cancellationToken);
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

        return result ?? ApiResult<T>.FailureResult($"伺服器回應無法解讀（HTTP {(int)response.StatusCode}）。", (int)response.StatusCode);
    }
}
