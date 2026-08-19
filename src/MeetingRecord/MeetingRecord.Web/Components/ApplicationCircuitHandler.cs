using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MeetingRecord.Web.Components;

/// <summary>
/// 紀錄 Blazor Server circuit 生命週期（開啟 / 連線起落 / 關閉），提升可觀測性。
/// 連線中斷以 Warning 記錄，便於追查使用者斷線與閃爍問題。
/// </summary>
public sealed class ApplicationCircuitHandler : CircuitHandler
{
    private readonly ILogger<ApplicationCircuitHandler> logger;

    public ApplicationCircuitHandler(ILogger<ApplicationCircuitHandler> logger)
    {
        this.logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogInformation("Blazor circuit opened. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogDebug("Blazor circuit connection up. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogWarning("Blazor circuit connection down (client may have disconnected). CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogInformation("Blazor circuit closed. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }
}
