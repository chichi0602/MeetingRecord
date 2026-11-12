using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;

namespace MeetingRecord.Web.Client.Pages.Monitoring;

/// <summary>
/// 日誌等級設定（0.4.120）。**管理者限定**。
///
/// <para>只改伺服器記憶體中 NLog 的 <c>&lt;logger name="*"&gt;</c> 規則，重新啟動就回到 nlog.config 的值。
/// 套用與還原都走 <c>api/v1/monitoring/log-level</c>，稽核由伺服器寫。</para>
/// </summary>
public partial class LogLevelSettingPage : ComponentBase
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

    [Inject] private MonitoringApi MonitoringApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private MessageService MessageService { get; set; } = default!;
    [Inject] private ILogger<LogLevelSettingPage> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private LogLevelRank selectedLevel = LogLevelRank.Info;
    private LogLevelRank effectiveLevel = LogLevelRank.Info;
    private LogLevelRank systemDefaultLevel = LogLevelRank.Info;
    private bool hasOverride;
    private bool isAvailable = true;
    private bool isAccessChecked;
    private bool isBusy;

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();

        // 管理者限定：這個權限鍵刻意不在角色權限目錄裡，所以直接看是不是管理者（API 也是 [AdminOnly]）。
        if (!PermissionView.IsAdmin)
        {
            isAccessChecked = true;
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/log-level-setting");
            Logger.LogWarning("Log level setting denied because the current user is not an administrator.");
            return;
        }

        var result = await MonitoringApi.GetLogLevelAsync();
        if (result.Success && result.Data is { } state)
        {
            ApplyState(state);
        }
        else
        {
            isAvailable = false;
            Logger.LogWarning("Loading log level state failed: {Message}", ApiClient.ErrorText(result));
        }

        isAccessChecked = true;
    }

    /// <summary>每次進頁與每次操作後都以伺服器回傳的真實狀態為準，不快取。</summary>
    private void ApplyState(LogLevelStateDto state)
    {
        isAvailable = state.IsAvailable;
        effectiveLevel = state.EffectiveLevel;
        systemDefaultLevel = state.SystemDefaultLevel;
        hasOverride = state.HasOverride;
        selectedLevel = effectiveLevel;
    }

    private async Task OnApplyAsync()
    {
        if (isBusy)
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

            var result = await MonitoringApi.ApplyLogLevelAsync(target);
            if (!result.Success || result.Data is not { } state)
            {
                _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
                return;
            }

            ApplyState(state);
            _ = MessageService.SuccessAsync($"已套用日誌等級：{LogLevelRankHelper.ToLevelText(target)}");
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task OnRestoreAsync()
    {
        if (isBusy)
        {
            return;
        }

        isBusy = true;
        try
        {
            var result = await MonitoringApi.RestoreLogLevelAsync();
            if (!result.Success || result.Data is not { } state)
            {
                _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
                return;
            }

            ApplyState(state);
            _ = MessageService.SuccessAsync($"已還原為系統預設等級：{LogLevelRankHelper.ToLevelText(effectiveLevel)}");
        }
        finally
        {
            isBusy = false;
        }
    }
}
