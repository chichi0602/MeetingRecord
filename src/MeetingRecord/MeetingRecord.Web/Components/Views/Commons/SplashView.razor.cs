using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Web.Components.Views.Commons;

public partial class SplashView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;
    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;
    [Inject]
    public ILogger<SplashView> Logger { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            Logger.LogDebug("Splash view running authentication check.");
            var checkResult = await AuthenticationStateHelper
            .Check(authStateProvider, NavigationManager);
            if (checkResult == AuthenticationCheckResult.Succeeded)
            {
                Logger.LogInformation("Splash view authentication succeeded. Redirecting to /app.");
                NavigationManager.NavigateTo("/app", true, true);
            }
            else
            {
                Logger.LogWarning("Splash view authentication check did not succeed. Result={CheckResult}", checkResult);
            }
        }
    }
}
