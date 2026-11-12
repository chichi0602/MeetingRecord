using Microsoft.Extensions.Logging;
using MeetingRecord.Web.Client.Services;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.54：WASM 頁上的 .NET 例外送到系統例外紀錄。只轉送 Blazor 元件框架記下、Error 以上、帶例外的記錄；
/// 登入狀態確認前暫存、接上時一次送出。
/// </summary>
public sealed class DotNetErrorForwarderTests
{
    private readonly List<(string Kind, string Message, string Stack, string? Path)> sent = [];

    [Fact]
    public void ComponentsError_WithException_ShouldBeSentAsDotnet()
    {
        var forwarder = Attached("/dashboard");
        var logger = new ClientErrorLoggerProvider(forwarder).CreateLogger("Microsoft.AspNetCore.Components.Web.ErrorBoundary");

        logger.LogError(new NullReferenceException("boom"), "unhandled");

        var item = Assert.Single(sent);
        Assert.Equal("dotnet", item.Kind);
        Assert.Equal("System.NullReferenceException: boom", item.Message);
        Assert.Contains("NullReferenceException", item.Stack);
        Assert.Equal("/dashboard", item.Path);
    }

    [Fact]
    public void RendererCritical_ShouldBeSent()
    {
        var forwarder = Attached("/meetings");
        var logger = new ClientErrorLoggerProvider(forwarder).CreateLogger("Microsoft.AspNetCore.Components.WebAssembly.Rendering.WebAssemblyRenderer");

        logger.LogCritical(new InvalidOperationException("render"), "unhandled");

        Assert.Single(sent);
    }

    [Fact]
    public void OtherCategories_LowerLevels_OrNoException_ShouldNotBeSent()
    {
        var provider = new ClientErrorLoggerProvider(Attached("/"));

        // 頁面自己記的錯誤（API 失敗等）伺服器端已有紀錄，不轉送。
        provider.CreateLogger("MeetingRecord.Web.Client.Pages.Meetings.MeetingPage").LogError(new Exception("page"), "x");
        var components = provider.CreateLogger("Microsoft.AspNetCore.Components.Web.ErrorBoundary");
        components.LogWarning(new Exception("warn"), "x");
        components.LogError("no exception");

        Assert.Empty(sent);
    }

    [Fact]
    public void BeforeAttach_ShouldBufferUpToTheLimit_ThenFlushOnAttach()
    {
        var forwarder = new DotNetErrorForwarder();
        for (var i = 0; i < DotNetErrorForwarder.MaxBuffered + 5; i++)
        {
            forwarder.Capture(new Exception($"e{i}"));
        }

        Assert.Equal(DotNetErrorForwarder.MaxBuffered, forwarder.BufferedCount);

        forwarder.Attach(Record, () => "/projects");

        Assert.Equal(DotNetErrorForwarder.MaxBuffered, sent.Count);
        Assert.All(sent, x => Assert.Equal("/projects", x.Path));
        Assert.Equal(0, forwarder.BufferedCount);
    }

    [Fact]
    public void LongMessages_ShouldBeTruncated()
    {
        var forwarder = Attached("/");

        forwarder.Capture(new Exception(new string('m', 5000)));

        var item = Assert.Single(sent);
        Assert.Equal(1000, item.Message.Length);
        Assert.True(item.Stack.Length <= 4000);
    }

    private DotNetErrorForwarder Attached(string path)
    {
        var forwarder = new DotNetErrorForwarder();
        forwarder.Attach(Record, () => path);
        return forwarder;
    }

    private Task Record(string kind, string message, string stack, string? path)
    {
        sent.Add((kind, message, stack, path));
        return Task.CompletedTask;
    }
}
