using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using MeetingRecord.Dtos.Systems;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Health;

/// <summary>「關於」對話窗的資料（1.1.6），由 <c>GET /api/v1/system/about</c> 提供給 WASM 前端。</summary>
public sealed class SystemAboutProvider(IOptions<SystemSettings> systemSettings, IWebHostEnvironment environment, SystemStartupState startupState)
{
    /// <summary>已運作時間在呼叫當下計算。</summary>
    public SystemAboutDto Get()
    {
        var info = systemSettings.Value.SystemInformation;
        return new SystemAboutDto
        {
            SystemName = info.SystemName,
            SystemDescription = info.SystemDescription,
            SystemVersion = info.SystemVersion,
            EnvironmentName = environment.EnvironmentName,
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            StartedAt = startupState.StartedAt,
            Uptime = DateTimeOffset.Now - startupState.StartedAt,
        };
    }
}
