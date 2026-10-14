using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NLog.Config;
using NLog.Web;
using NLogManager = NLog.LogManager;

namespace MeetingRecord.Tests;

/// <summary>
/// appsettings 的「NLog」區段（只放 <c>NLog:BasePath</c>）不能被 NLog 當成設定載入（0.4.123）。
///
/// 會動到行程層級的 NLog 設定，所以放進 <see cref="NLogConfigurationCollection"/> 序列化執行，結束時還原。
/// </summary>
[Collection(nameof(NLogConfigurationCollection))]
public sealed class NLogOptionsTests : IDisposable
{
    private readonly LoggingConfiguration? originalConfiguration = NLogManager.Configuration;

    public void Dispose() => NLogManager.Configuration = originalConfiguration;

    [Fact]
    public void DefaultOptions_WithEmptyNLogConfiguration_ShouldChokeOnBasePath()
    {
        // 重現整合測試的偶發失敗：另一個主機 Shutdown 後行程共用設定是空的，NLog 轉而解析 appsettings 的 NLog 區段。
        NLogManager.Configuration = new LoggingConfiguration();

        Assert.ThrowsAny<NLog.NLogConfigurationException>(() => BuildHost(new NLogAspNetCoreOptions()));
    }

    [Fact]
    public void ProgramOptions_WithEmptyNLogConfiguration_ShouldNotLoadTheAppSettingsSection()
    {
        NLogManager.Configuration = new LoggingConfiguration();

        using var host = BuildHost(MeetingRecord.Web.Program.NLogOptions);

        Assert.NotNull(host);
    }

    private static IHost BuildHost(NLogAspNetCoreOptions options)
    {
        return new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NLog:BasePath"] = Path.Combine(Path.GetTempPath(), "MeetingRecordNLogOptionsTests"),
            }))
            .UseNLog(options)
            .Build();
    }
}
