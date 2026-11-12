using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Components.Layout;

/// <summary>
/// 全站共用的外框（1.1.5 起），由 <c>WasmLayout</c> 掛上；資料（選單、使用者、關於）與動作（變更密碼）都由外面傳入，
/// 這裡不直接打 API。
/// </summary>
public partial class AppShell : ComponentBase, IDisposable
{
    private const string DefaultPageTitle = "系統首頁";

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>右上角的通知鈴。</summary>
    [Parameter]
    public RenderFragment? NotificationContent { get; set; }

    /// <summary>常駐浮層（WasmLayout 放進來的背景工作進度面板）。</summary>
    [Parameter]
    public RenderFragment? OverlayContent { get; set; }

    /// <summary>已依權限過濾好的選單；頁面標題也從這裡比對目前網址。</summary>
    [Parameter]
    public IReadOnlyList<MenuItemDto> MenuItems { get; set; } = [];

    [Parameter]
    public string SystemName { get; set; } = string.Empty;

    [Parameter]
    public string UserDisplayName { get; set; } = "使用者";

    [Parameter]
    public bool IsAdmin { get; set; }

    /// <summary>系統預設帳號（不允許在網頁變更密碼）。</summary>
    [Parameter]
    public bool IsSupportAccount { get; set; }

    /// <summary>變更密碼：參數是目前密碼與新密碼，成功回 null、失敗回錯誤訊息。null 表示不提供這個功能（選單不顯示）。</summary>
    [Parameter]
    public Func<string, string, Task<string?>>? ChangePasswordAsync { get; set; }

    /// <summary>「關於」對話窗的內容；開啟當下才取（已運作時間要即時）。null 表示不提供。</summary>
    [Parameter]
    public Func<Task<IReadOnlyList<KeyValuePair<string, string>>>>? LoadAboutAsync { get; set; }

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private MessageService MessageService { get; set; } = default!;

    [Inject]
    private ILogger<AppShell> Logger { get; set; } = default!;

    private string CurrentPageTitle { get; set; } = DefaultPageTitle;
    private bool isSidebarCollapsed = true;
    private bool isUserMenuOpen;

    private bool changePasswordVisible;
    private string changePasswordErrorMessage = string.Empty;
    private ChangePasswordForm changePasswordForm = new();

    /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
    private string? formSnapshot;

    /// <summary>Esc 會同時走 AntDesign 的 Keyboard 與表單的 keydown，少了旗標會疊出兩個確認框。</summary>
    private bool isDiscardConfirming;

    private bool aboutVisible;
    private IReadOnlyList<KeyValuePair<string, string>> aboutItems = [];

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    protected override void OnParametersSet()
    {
        UpdateCurrentPageTitle();
    }

    private void UpdateCurrentPageTitle()
    {
        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Split('?', '#')[0].Trim('/');
        var normalizedCurrentPath = string.IsNullOrEmpty(currentPath) ? "/" : $"/{currentPath}";

        CurrentPageTitle = TryFindMenuTitle(MenuItems, normalizedCurrentPath, out var pageTitle)
            ? pageTitle
            : DefaultPageTitle;
    }

    private static bool TryFindMenuTitle(IEnumerable<MenuItemDto> items, string currentPath, out string pageTitle)
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
        isUserMenuOpen = false;
        UpdateCurrentPageTitle();
        InvokeAsync(StateHasChanged);
    }

    private void OnChangePasswordClick()
    {
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
        if (IsSupportAccount || ChangePasswordAsync is null)
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

        var error = await ChangePasswordAsync(changePasswordForm.CurrentPassword, changePasswordForm.NewPassword);
        if (error is not null)
        {
            changePasswordErrorMessage = error;
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
                // 開關由本頁決定（1.1.19 起視窗是單向繫結）：按「繼續編修」就維持開著。
                changePasswordVisible = true;
                return;
            }
        }

        changePasswordVisible = false;
        formSnapshot = null;
        changePasswordErrorMessage = string.Empty;
    }

    /// <summary>開啟「關於」對話窗。內容在開啟當下才取，已運作時間才會是即時的。</summary>
    private async Task OnAboutClick()
    {
        if (LoadAboutAsync is null)
        {
            return;
        }

        Logger.LogDebug("Opening about dialog.");
        aboutItems = await LoadAboutAsync();
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
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
