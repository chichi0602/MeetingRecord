using System.Net;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Web.Auth;

/// <summary>
/// 來源 IP（1.0.32，稽核紀錄用，移植自 NET10-Blazor-Starter）。Scoped：一個 HTTP 請求或一條 Blazor 連線一份。
///
/// <para>取值順序：Blazor 連線建立時記下的 IP → 目前 <see cref="HttpContext"/> 的連線 IP。
/// 互動式 Blazor 的事件處理沒有可靠的 HttpContext，所以由 <c>ApplicationCircuitHandler</c> 在連線建立（含重連）時呼叫
/// <see cref="SetCircuitClientIp"/> 先記下來。反向代理後面靠 <c>UseForwardedHeaders</c> 換成真實 IP。</para>
/// <para>⚠️ DI 註冊時具體類別與 <see cref="IClientIpProvider"/> 必須解析到<b>同一個實例</b>，否則記下的 IP 讀不到。</para>
/// </summary>
public sealed class ClientIpProvider : IClientIpProvider
{
    private readonly IHttpContextAccessor httpContextAccessor;
    private string? circuitClientIp;

    public ClientIpProvider(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    public string? GetClientIp()
        => circuitClientIp ?? Normalize(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);

    /// <summary>Blazor 連線建立或重連時記下 IP；拿不到就保留上一次的值。</summary>
    public void SetCircuitClientIp(IPAddress? address)
    {
        var ip = Normalize(address);
        if (ip is not null)
        {
            circuitClientIp = ip;
        }
    }

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
