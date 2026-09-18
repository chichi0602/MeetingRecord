using System.Runtime.InteropServices;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Health;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Components.Layout;

public partial class MainLayout : LayoutComponentBase, IDisposable
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

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
    private MessageService MessageService { get; set; } = default!;

    [Inject]
    private IOptions<SystemSettings> SystemSettingsOptions { get; set; } = default!;

    [Inject]
    private IWebHostEnvironment WebHostEnvironment { get; set; } = default!;

    [Inject]
    private SystemStartupState SystemStartupState { get; set; } = default!;

    private const string DefaultPageTitle = "系統首頁";
    private const string DefaultUserDisplayName = "使用者";

    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];
    private string CurrentPageTitle { get; set; } = DefaultPageTitle;
    private string CurrentUserDisplayName { get; set; } = DefaultUserDisplayName;
    private bool CurrentUserIsAdmin { get; set; }
    private bool isSidebarCollapsed = true;
    private bool isUserMenuOpen;

    private bool changePasswordVisible = false;
    private bool isSupportAccount = false;
    private string changePasswordErrorMessage = string.Empty;
    private ChangePasswordForm changePasswordForm = new();

    /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
    private string? formSnapshot;

    /// <summary>Esc 會同時走 AntDesign 的 Keyboard 與表單的 keydown，少了旗標會疊出兩個確認框。</summary>
    private bool isDiscardConfirming;

    private bool aboutVisible = false;
    private IReadOnlyList<KeyValuePair<string, string>> aboutItems = [];

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing main layout.");

        var checkResult = await AuthenticationStateHelper.Check(AuthenticationStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            MenuItems = [];
            return;
        }

        MenuItems = await SidebarMenuService.LoadAuthorizedMenuItemsAsync(AuthenticationStateHelper);
        UpdateCurrentUserStatus();
        UpdateCurrentPageTitle();
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private void UpdateCurrentUserStatus()
    {
        var currentUser = CurrentUserService.CurrentUser;

        CurrentUserDisplayName = !string.IsNullOrWhiteSpace(currentUser.Name)
            ? currentUser.Name
            : !string.IsNullOrWhiteSpace(currentUser.Account)
                ? currentUser.Account
                : DefaultUserDisplayName;

        CurrentUserIsAdmin = currentUser.IsAdmin;
    }

    private void UpdateCurrentPageTitle()
    {
        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Trim('/');
        var normalizedCurrentPath = string.IsNullOrEmpty(currentPath) ? "/" : $"/{currentPath}";

        CurrentPageTitle = TryFindMenuTitle(MenuItems, normalizedCurrentPath, out var pageTitle)
            ? pageTitle
            : DefaultPageTitle;

        Logger.LogDebug("Updated page title. Path={Path}, Title={Title}", normalizedCurrentPath, CurrentPageTitle);
    }

    private static bool TryFindMenuTitle(IEnumerable<SidebarMenuItemModel> items, string currentPath, out string pageTitle)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Url) && IsMatchingUrl(item.Url, currentPath))
            {
                pageTitle = item.Name;
                return true;
            }

            if (item.HasChildren && TryFindMenuTitle(item.SubMenu, currentPath, out pageTitle))
            {
                return true;
            }
        }

        pageTitle = string.Empty;
        return false;
    }

    private static bool IsMatchingUrl(string url, string currentPath)
    {
        var normalizedTargetPath = url.Trim();
        if (string.IsNullOrEmpty(normalizedTargetPath))
        {
            return false;
        }

        normalizedTargetPath = normalizedTargetPath.StartsWith('/') ? normalizedTargetPath : $"/{normalizedTargetPath}";
        if (string.Equals(normalizedTargetPath, "/", StringComparison.Ordinal))
        {
            return string.Equals(currentPath, "/", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(currentPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith($"{normalizedTargetPath}/", StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        Logger.LogDebug("Location changed in main layout. Uri={Uri}", e.Location);
        isUserMenuOpen = false;
        UpdateCurrentPageTitle();
        InvokeAsync(StateHasChanged);
    }

    private void OnChangePasswordClick()
    {
        var supportAccount = Configuration["BootstrapSettings:SupportAccount"] ?? "support";
        isSupportAccount = CurrentUserService.CurrentUser.Account == supportAccount;
        changePasswordForm = new ChangePasswordForm();
        changePasswordErrorMessage = string.Empty;
        formSnapshot = FormDirtyHelper.Capture(changePasswordForm);
        changePasswordVisible = true;
    }

    /// <summary>
    /// 變更密碼的 Enter 送出。密碼欄不會觸發輸入法，但仍走共用判斷——
    /// 規則只有一份，才不會有人日後在這裡補出第二種寫法。
    /// </summary>
    private async Task OnChangePasswordKeyDownAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs args)
    {
        if (FormKeyboardHelper.IsSubmit(args))
        {
            await OnChangePasswordOkAsync();
        }
    }

    private async Task OnChangePasswordOkAsync()
    {
        if (isSupportAccount)
        {
            changePasswordVisible = false;
            return;
        }

        changePasswordErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(changePasswordForm.CurrentPassword))
        {
            changePasswordErrorMessage = "請輸入目前密碼。";
            changePasswordVisible = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(changePasswordForm.NewPassword) || changePasswordForm.NewPassword.Length < 6)
        {
            changePasswordErrorMessage = "新密碼至少需要 6 個字元。";
            changePasswordVisible = true;
            return;
        }

        if (changePasswordForm.NewPassword != changePasswordForm.ConfirmPassword)
        {
            changePasswordErrorMessage = "新密碼與確認密碼不一致。";
            changePasswordVisible = true;
            return;
        }

        var userId = CurrentUserService.CurrentUser.Id;
        var result = await MyUserService.ChangePasswordAsync(userId, changePasswordForm.CurrentPassword, changePasswordForm.NewPassword);

        if (!result.Success)
        {
            changePasswordErrorMessage = result.Message ?? "變更密碼失敗，請稍後再試。";
            changePasswordVisible = true;
            return;
        }

        changePasswordVisible = false;
        _ = MessageService.SuccessAsync("密碼變更成功！");
    }

    private async Task OnChangePasswordCancelAsync()
    {
        if (isDiscardConfirming)
        {
            return;
        }

        // support 帳號時表單根本沒渲染（只有一張 Alert），快照與比對都是空表單 → 不會問。
        if (FormDirtyHelper.IsDirty(formSnapshot, changePasswordForm))
        {
            isDiscardConfirming = true;
            bool discard;
            try
            {
                discard = await FormDirtyHelper.ConfirmDiscardAsync(ModalService, "密碼變更表單");
            }
            finally
            {
                isDiscardConfirming = false;
            }

            if (!discard)
            {
                // ⚠️ @bind-Visible 是雙向的，AntDesign 已經把視窗關掉了；不重開會失去已填的內容。
                changePasswordVisible = true;
                return;
            }
        }

        changePasswordVisible = false;
        formSnapshot = null;
        changePasswordErrorMessage = string.Empty;
    }

    /// <summary>
    /// 開啟「關於」對話窗。已運作時間需在開啟當下計算，Blazor Server 不會自動刷新已渲染的值。
    /// </summary>
    private void OnAboutClick()
    {
        Logger.LogDebug("Opening about dialog.");

        var systemInformation = SystemSettingsOptions.Value.SystemInformation;
        var uptime = DateTimeOffset.Now - SystemStartupState.StartedAt;

        aboutItems =
        [
            new("系統名稱", systemInformation.SystemName),
            new("系統描述", systemInformation.SystemDescription),
            new("系統版本", systemInformation.SystemVersion),
            new("執行環境", WebHostEnvironment.EnvironmentName),
            new(".NET 版本", RuntimeInformation.FrameworkDescription),
            new("啟動時間", SystemStartupState.StartedAt.ToString("yyyy/MM/dd HH:mm:ss")),
            new("已運作時間", uptime.ToString(@"dd\.hh\:mm\:ss")),
        ];

        isUserMenuOpen = false;
        aboutVisible = true;
    }

    private void OnAboutCancel()
    {
        aboutVisible = false;
    }

    private void ToggleSidebar()
    {
        isSidebarCollapsed = !isSidebarCollapsed;
    }

    private void ToggleUserMenu()
    {
        isUserMenuOpen = !isUserMenuOpen;
    }

    private sealed class ChangePasswordForm
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        Logger.LogDebug("Disposing main layout.");
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
