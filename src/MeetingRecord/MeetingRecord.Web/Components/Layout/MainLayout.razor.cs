using System.Runtime.InteropServices;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Health;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Components.Layout;

/// <summary>
/// Blazor Server 頁的版面：外觀交給共用的 <see cref="AppShell"/>（1.1.5），這裡供應伺服器端的資料與動作。
/// </summary>
public partial class MainLayout : LayoutComponentBase, IDisposable
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private ILogger<MainLayout> Logger { get; set; } = default!;

    [Inject]
    private SidebarMenuService SidebarMenuService { get; set; } = default!;

    [Inject]
    private IConfiguration Configuration { get; set; } = default!;

    [Inject]
    private MyUserService MyUserService { get; set; } = default!;

    [Inject]
    private IOptions<SystemSettings> SystemSettingsOptions { get; set; } = default!;

    [Inject]
    private IWebHostEnvironment WebHostEnvironment { get; set; } = default!;

    [Inject]
    private SystemStartupState SystemStartupState { get; set; } = default!;

    [Inject]
    private BrowserErrorReporter BrowserErrorReporter { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    /// <summary>瀏覽器錯誤回報的 .NET 參考（0.4.122）；登入成功後才建立。</summary>
    private DotNetObjectReference<BrowserErrorReporter>? browserErrorReporterReference;

    private bool isAuthenticated;

    private const string DefaultUserDisplayName = "使用者";

    private List<MenuItemDto> menuItems = [];
    private string currentUserDisplayName = DefaultUserDisplayName;
    private bool currentUserIsAdmin;
    private bool isSupportAccount;

    /// <summary>右上角鈴鐺（1.0.44）要知道是誰；登入檢查完成前是 0，鈴鐺就先不查。</summary>
    private int currentUserId;

    /// <summary>側邊欄顯示的系統名稱。以設定檔為單一來源，不寫死在標記裡。</summary>
    private string SystemName => SystemSettingsOptions.Value.SystemInformation.SystemName;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing main layout.");

        var checkResult = await AuthenticationStateHelper.Check(AuthenticationStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        menuItems = MenuItemMapping.ToDtos(await SidebarMenuService.LoadAuthorizedMenuItemsAsync(AuthenticationStateHelper));
        UpdateCurrentUserStatus();
        isAuthenticated = true;
    }

    /// <summary>
    /// 登入後才註冊瀏覽器錯誤回報（0.4.122）：只有已登入、有 circuit 的頁面回報，匿名者無從灌資料。
    /// isAuthenticated 在非同步初始化後才成立，所以不能只看 firstRender。
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!isAuthenticated || browserErrorReporterReference is not null || !BrowserErrorReporter.IsEnabled)
        {
            return;
        }

        browserErrorReporterReference = DotNetObjectReference.Create(BrowserErrorReporter);
        try
        {
            await JSRuntime.InvokeVoidAsync("meetingRecordClientErrors.register", browserErrorReporterReference);
        }
        catch (JSDisconnectedException)
        {
            // circuit 已斷線，沒有東西可註冊。
        }
        catch (JSException ex)
        {
            // 腳本沒載入（例如被瀏覽器外掛擋掉）：少了前端錯誤回報，頁面照常可用。
            Logger.LogWarning(ex, "Failed to register the browser error reporter.");
        }
    }

    private void UpdateCurrentUserStatus()
    {
        var currentUser = CurrentUserService.CurrentUser;

        currentUserDisplayName = !string.IsNullOrWhiteSpace(currentUser.Name)
            ? currentUser.Name
            : !string.IsNullOrWhiteSpace(currentUser.Account)
                ? currentUser.Account
                : DefaultUserDisplayName;

        currentUserIsAdmin = currentUser.IsAdmin;
        currentUserId = currentUser.Id;

        var supportAccount = Configuration["BootstrapSettings:SupportAccount"] ?? "support";
        isSupportAccount = currentUser.Account == supportAccount;
    }

    /// <summary>AppShell 已檢查過輸入（目前密碼必填、新密碼至少 6 字、兩次一致）；這裡只呼叫服務。成功回 null。</summary>
    private async Task<string?> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var result = await MyUserService.ChangePasswordAsync(CurrentUserService.CurrentUser.Id, currentPassword, newPassword);
        return result.Success ? null : result.Message ?? "變更密碼失敗，請稍後再試。";
    }

    /// <summary>「關於」的內容，開啟當下才算：已運作時間 Blazor Server 不會自動刷新已渲染的值。</summary>
    private Task<IReadOnlyList<KeyValuePair<string, string>>> LoadAboutAsync()
    {
        var systemInformation = SystemSettingsOptions.Value.SystemInformation;
        var uptime = DateTimeOffset.Now - SystemStartupState.StartedAt;

        IReadOnlyList<KeyValuePair<string, string>> items =
        [
            new("系統名稱", systemInformation.SystemName),
            new("系統描述", systemInformation.SystemDescription),
            new("系統版本", systemInformation.SystemVersion),
            new("執行環境", WebHostEnvironment.EnvironmentName),
            new(".NET 版本", RuntimeInformation.FrameworkDescription),
            new("啟動時間", SystemStartupState.StartedAt.ToString("yyyy/MM/dd HH:mm:ss")),
            new("已運作時間", uptime.ToString(@"dd\.hh\:mm\:ss")),
        ];
        return Task.FromResult(items);
    }

    public void Dispose()
    {
        browserErrorReporterReference?.Dispose();
    }
}
