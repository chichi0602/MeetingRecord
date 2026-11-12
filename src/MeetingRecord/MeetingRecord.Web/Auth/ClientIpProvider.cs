using System.Net;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Web.Auth;

/// <summary>
/// 來源 IP（1.0.32，稽核紀錄用，移植自 NET10-Blazor-Starter）。Scoped：一個 HTTP 請求一份，取目前 <see cref="HttpContext"/> 的連線 IP；
/// 反向代理後面靠 <c>UseForwardedHeaders</c> 換成真實 IP。
/// </summary>
public sealed class ClientIpProvider : IClientIpProvider
{
    private readonly IHttpContextAccessor httpContextAccessor;

    public ClientIpProvider(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    public string? GetClientIp() => Normalize(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);

    /// <summary>IPv4-mapped IPv6（<c>::ffff:1.2.3.4</c>）轉回 IPv4，篩選時才能用 <c>1.2.3.</c> 這種前綴。</summary>
    internal static string? Normalize(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
