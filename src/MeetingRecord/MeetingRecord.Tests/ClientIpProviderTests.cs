using System.Net;
using Microsoft.AspNetCore.Http;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Tests;

/// <summary>稽核紀錄的來源 IP（1.0.32）。</summary>
public sealed class ClientIpProviderTests
{
    [Fact]
    public void GetClientIp_ShouldUseHttpContextConnection()
    {
        var provider = new ClientIpProvider(Accessor("198.51.100.7"));

        Assert.Equal("198.51.100.7", provider.GetClientIp());
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
