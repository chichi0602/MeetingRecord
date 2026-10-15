using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.Components.Views.Monitoring;

/// <summary>
/// 日誌等級設定（0.4.120，移植自 NET10-Blazor-Starter）。**管理者限定**。
///
/// <para>只改記憶體中 NLog 的 <c>&lt;logger name="*"&gt;</c> 規則（<see cref="LogLevelRuntimeState"/>），
/// 重新啟動就回到 nlog.config 的值；套用與還原都寫稽核。</para>
/// </summary>
public partial class LogLevelSettingView : ComponentBase
{
    private static readonly (LogLevelRank Level, string Description)[] LevelOptions =
    [
        (LogLevelRank.Trace, "最詳細，含框架層級細節"),
        (LogLevelRank.Debug, "含查詢條件、換頁等排錯資訊"),
        (LogLevelRank.Info, "含使用者操作軌跡，日常建議值"),
        (LogLevelRank.Warn, "僅警告以上"),
        (LogLevelRank.Error, "僅錯誤以上"),
        (LogLevelRank.Fatal, "僅嚴重錯誤"),
    ];

    [Inject]
    private LogLevelRuntimeState LogLevelState { get; set; } = default!;

    [Inject]
    private IAuditLogService AuditLogService { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private MessageService MessageService { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<LogLevelSettingView> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private LogLevelRank selectedLevel = LogLevelRank.Info;
    private LogLevelRank effectiveLevel = LogLevelRank.Info;
    private LogLevelRank systemDefaultLevel = LogLevelRank.Info;
    private bool hasOverride;
    private bool isAccessChecked;
    private bool isBusy;

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        // 管理者限定：這個權限鍵刻意不在角色權限目錄裡，所以直接看 CheckIsAdmin。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Log level setting denied because the current user is not an administrator.");
            return;
        }

        RefreshState();
        selectedLevel = effectiveLevel;
    }

    /// <summary>每次進頁與每次操作後都重讀真實狀態，不快取。</summary>
    private void RefreshState()
    {
        effectiveLevel = LogLevelState.EffectiveLevel;
        systemDefaultLevel = LogLevelState.SystemDefaultLevel;
        hasOverride = LogLevelState.OverrideLevel is not null;
    }

    private async Task OnApplyAsync()
    {
        if (isBusy || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        isBusy = true;
        try
        {
            var target = selectedLevel;

            // 只有調低到會暴增日誌量的等級才需要確認；調高不會有這個風險。
            if (target is LogLevelRank.Trace or LogLevelRank.Debug)
            {
                var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
                {
                    Title = "確認調整日誌等級",
                    Content = $"調整為 {LogLevelRankHelper.ToLevelText(target)} 會大幅增加日誌量，排查完問題後請記得調回來。",
                    OkText = "套用",
                    CancelText = "取消",
                    MaskClosable = false,
                });

                if (!confirmed)
                {
                    return;
                }
            }

            var previous = effectiveLevel;
            if (!LogLevelState.Apply(target))
            {
                _ = MessageService.ErrorAsync("套用失敗，請確認 NLog 設定。");
                return;
            }

            RefreshState();
            selectedLevel = effectiveLevel;
            _ = MessageService.SuccessAsync($"已套用日誌等級：{LogLevelRankHelper.ToLevelText(target)}");

            await WriteAuditAsync(AuditActions.LogLevelApply, previous, target);
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task OnRestoreAsync()
    {
        if (isBusy || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        isBusy = true;
        try
        {
            var previous = effectiveLevel;
            if (!LogLevelState.RestoreDefault())
            {
                _ = MessageService.ErrorAsync("還原失敗，請確認 NLog 設定。");
                return;
            }

            RefreshState();
            selectedLevel = effectiveLevel;
            _ = MessageService.SuccessAsync($"已還原為系統預設等級：{LogLevelRankHelper.ToLevelText(effectiveLevel)}");

            await WriteAuditAsync(AuditActions.LogLevelRestore, previous, effectiveLevel);
        }
        finally
        {
            isBusy = false;
        }
    }

    /// <summary>稽核寫入失敗不推翻「等級已經套用」這個事實（AuditLogService 本身也不會拋例外）。</summary>
    private Task WriteAuditAsync(string action, LogLevelRank previous, LogLevelRank current)
    {
        var currentUser = CurrentUserService.CurrentUser;
        return AuditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: currentUser.Id,
            actorAccount: currentUser.Account,
            targetType: "NLog",
            targetId: "logger:*",
            detail: $"日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} 調整為 {LogLevelRankHelper.ToLevelText(current)}（僅執行期有效）。");
    }
}
