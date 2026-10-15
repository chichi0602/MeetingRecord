using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.0.29 起所有付費 AI 呼叫都經過 <see cref="AiCallGateway"/>。測試共用的組裝方式：
/// 帳本用真的 <see cref="AiUsageRecorder"/>（與被測物共用同一個 context，「記帳會不會提前提交別人的變更」正是要測的事），
/// 對話紀錄與用量上限預設換成可觀察的假物件。
/// </summary>
internal static class TestAiServices
{
    public static AiCallGateway CreateGateway(
        BackendDBContext context,
        IOptions<LlmSettings> llmSettings,
        IAiCallLogRecorder? callLogRecorder = null,
        IAiQuotaService? quotaService = null,
        ExchangeRateCache? exchangeRates = null)
        => new(
            new AiUsageRecorder(context, llmSettings, exchangeRates ?? new ExchangeRateCache(), NullLogger<AiUsageRecorder>.Instance),
            callLogRecorder ?? new RecordingAiCallLogRecorder(),
            quotaService ?? new FakeAiQuotaService(),
            TestAuditLogs.CreateService(context),
            NullLogger<AiCallGateway>.Instance);

    /// <summary>真的 <see cref="AiCallLogService"/>（刪除會議／專案的連帶刪除要用）。</summary>
    public static AiCallLogService CreateCallLogService(
        BackendDBContext context,
        string? aiCallLogPath = null,
        AiCallLogSettings? settings = null)
    {
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.AiCallLogPath = aiCallLogPath ?? string.Empty;

        return new AiCallLogService(
            context,
            new AiCallLogFileStore(Options.Create(systemSettings), NullLogger<AiCallLogFileStore>.Instance),
            new StaticOptionsMonitor<AiCallLogSettings>(settings ?? new AiCallLogSettings()),
            NullLogger<AiCallLogService>.Instance);
    }
}

/// <summary>把寫進來的對話紀錄留在記憶體，讓測試檢查內容。</summary>
internal sealed class RecordingAiCallLogRecorder : IAiCallLogRecorder
{
    public bool IsEnabled { get; set; } = true;

    public bool ThrowOnRecord { get; set; }

    public List<AiCallLogEntry> Entries { get; } = [];

    public Task RecordAsync(AiCallLogEntry entry)
    {
        if (ThrowOnRecord)
        {
            throw new InvalidOperationException("recorder down");
        }

        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

/// <summary>可指定「已達上限」的假上限服務。預設不限制。</summary>
internal sealed class FakeAiQuotaService : IAiQuotaService
{
    public AiQuotaStatus? Blocked { get; set; }

    public int CheckCount { get; private set; }

    public static AiQuotaStatus GlobalReached(int limit = 100, decimal used = 120)
        => new(AiQuotaScope.Global, limit, used, new DateTime(2026, 10, 1), new DateTime(2026, 11, 1));

    public Task<AiQuotaStatus?> CheckAsync(int? userId, CancellationToken cancellationToken = default)
    {
        CheckCount++;
        return Task.FromResult(Blocked);
    }

    public Task<IReadOnlyList<AiQuotaStatus>> GetStatusAsync(int? userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AiQuotaStatus>>(Blocked is null ? [] : [Blocked]);

    public Task<IReadOnlyList<AiQuotaUserUsage>> GetTopUsersAsync(int take, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AiQuotaUserUsage>>([]);
}

/// <summary>固定值的 <see cref="IOptionsMonitor{TOptions}"/>。</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
