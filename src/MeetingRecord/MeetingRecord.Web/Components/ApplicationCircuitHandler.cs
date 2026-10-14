using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.Circuits;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.Components;

/// <summary>
/// 紀錄 Blazor Server circuit 生命週期（開啟 / 連線起落 / 關閉），提升可觀測性。
/// 連線中斷以 Warning 記錄，便於追查使用者斷線與閃爍問題。
///
/// <para>0.4.122 起另外負責「系統例外紀錄」的畫面情境（移植自 NET10-Blazor-Starter）：
/// 每次互動前把「誰、在哪一頁」放進 <see cref="ExceptionContextAccessor"/>，並給這次互動一組錯誤追蹤碼
/// （<see cref="TraceCode"/>）——circuit 內沒有 HttpContext，否則同一次操作的日誌串不起來。</para>
///
/// <para>身分取自 <see cref="AuthenticationStateProvider"/> 而不是 <c>CurrentUserService</c>：後者要等頁面的
/// <c>AuthenticationStateHelper.Check()</c> 跑過才有值。⚠️ Cookie 的 claim：<c>Sid</c>＝使用者編號、
/// <c>NameIdentifier</c>＝帳號、<c>Name</c>＝**姓名（個資，不記錄）**。</para>
/// </summary>
public sealed class ApplicationCircuitHandler : CircuitHandler, IDisposable
{
    private readonly ILogger<ApplicationCircuitHandler> logger;
    private readonly NavigationManager navigationManager;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly ExceptionContextAccessor exceptionContextAccessor;

    private string circuitId = string.Empty;
    private string currentPath = "/";
    private string? account;
    private int? userId;
    private bool subscribed;

    public ApplicationCircuitHandler(
        ILogger<ApplicationCircuitHandler> logger,
        NavigationManager navigationManager,
        AuthenticationStateProvider authenticationStateProvider,
        ExceptionContextAccessor exceptionContextAccessor)
    {
        this.logger = logger;
        this.navigationManager = navigationManager;
        this.authenticationStateProvider = authenticationStateProvider;
        this.exceptionContextAccessor = exceptionContextAccessor;
    }

    /// <summary>
    /// 每一次 inbound circuit activity（按鈕點擊、表單送出、元件生命週期回呼）之前設定例外情境與追蹤碼，
    /// 該次活動中任何 <c>logger.LogError(ex, …)</c> 都會帶上使用者與頁面。
    /// ⚠️ 全站每一次互動都會經過這裡：情境設定失敗絕不可連帶弄壞使用者正在做的事。
    /// </summary>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            try
            {
                exceptionContextAccessor.Set(new ExceptionContext(ExceptionSources.Ui, currentPath, account, userId));
            }
            catch (Exception ex)
            {
                // Warning 不會進例外紀錄，不會遞迴。
                logger.LogWarning(ex, "Failed to set exception context for circuit activity. CircuitId={CircuitId}", circuitId);
            }

            using var traceScope = TraceCode.Begin(TraceCode.New());

            await next(context);
        };
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        circuitId = circuit.Id;
        currentPath = ToRelativePath(navigationManager.Uri);
        await ResolveIdentityAsync();

        navigationManager.LocationChanged += OnLocationChanged;
        subscribed = true;

        logger.LogInformation("Blazor circuit opened. CircuitId={CircuitId}", circuit.Id);
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
        Unsubscribe();
        logger.LogInformation("Blazor circuit closed. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
        => currentPath = ToRelativePath(args.Location);

    private async Task ResolveIdentityAsync()
    {
        try
        {
            var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
            if (principal.Identity?.IsAuthenticated != true)
            {
                return;
            }

            account = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            userId = int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var id) && id > 0 ? id : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve identity for circuit. CircuitId={CircuitId}", circuitId);
        }
    }

    /// <summary>只留路徑、去掉查詢字串（可能含個資或權杖）。</summary>
    private string ToRelativePath(string uri)
    {
        try
        {
            var relative = navigationManager.ToBaseRelativePath(uri);
            var queryIndex = relative.IndexOfAny(['?', '#']);
            if (queryIndex >= 0)
            {
                relative = relative[..queryIndex];
            }

            return "/" + relative;
        }
        catch (ArgumentException)
        {
            return "(external)";
        }
    }

    private void Unsubscribe()
    {
        if (subscribed)
        {
            navigationManager.LocationChanged -= OnLocationChanged;
            subscribed = false;
        }
    }

    public void Dispose() => Unsubscribe();
}
