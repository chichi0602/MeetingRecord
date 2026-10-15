using System.Net;
using Microsoft.AspNetCore.Http;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Tests;

/// <summary>稽核紀錄的來源 IP（1.0.32）。</summary>
public sealed class ClientIpProviderTests
{
    [Fact]
    public void GetClientIp_WithoutCircuitIp_ShouldUseHttpContextConnection()
    {
        var provider = new ClientIpProvider(Accessor("198.51.100.7"));

        Assert.Equal("198.51.100.7", provider.GetClientIp());
    }

    /// <summary>互動式 Blazor 的事件裡沒有可靠的 HttpContext：連線建立時記下的 IP 優先。</summary>
    [Fact]
    public void GetClientIp_WithCircuitIp_ShouldPreferIt()
    {
        var provider = new ClientIpProvider(Accessor("198.51.100.7"));

        provider.SetCircuitClientIp(IPAddress.Parse("203.0.113.5"));

        Assert.Equal("203.0.113.5", provider.GetClientIp());
    }

    [Fact]
    public void SetCircuitClientIp_WithNull_ShouldKeepThePreviousValue()
    {
        var provider = new ClientIpProvider(Accessor(null));
        provider.SetCircuitClientIp(IPAddress.Parse("203.0.113.5"));

        provider.SetCircuitClientIp(null);

        Assert.Equal("203.0.113.5", provider.GetClientIp());
    }

    [Fact]
    public void GetClientIp_ShouldMapIpv4MappedIpv6BackToIpv4()
    {
        var provider = new ClientIpProvider(Accessor("::ffff:192.168.1.20"));

        Assert.Equal("192.168.1.20", provider.GetClientIp());
    }

    [Fact]
    public void GetClientIp_WithoutAnyConnection_ShouldReturnNull()
    {
        var provider = new ClientIpProvider(new HttpContextAccessor());

        Assert.Null(provider.GetClientIp());
    }

    private static HttpContextAccessor Accessor(string? remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
        return new HttpContextAccessor { HttpContext = context };
    }
}
