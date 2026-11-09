using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// 每個 API 請求都帶 <see cref="MagicObjectHelper.ClientRequestHeader"/>（1.1.1）：伺服器對「以 Cookie 驗證、會改資料」的請求
/// 要求這個標頭（CSRF 防護，見 Web 的 CookieApiCsrfFilter）。讀取請求帶著也無妨，一律加省得漏。
/// </summary>
public sealed class ClientHeaderHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(MagicObjectHelper.ClientRequestHeader, "1");
        return base.SendAsync(request, cancellationToken);
    }
}
