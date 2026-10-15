using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Components.Views.Admins;

/// <summary>
/// 稽核紀錄（1.0.32，移植自 NET10-Blazor-Starter 的 /audit-logs，畫面依本專案規範）。**只限管理者**。
///
/// <para>資料由 <c>IAuditLogService</c> 在各業務流程寫入。本頁負責查詢、明細、匯出 CSV、清除 N 天前與全部清除；
/// 刻意<b>沒有</b>單筆刪除。匯出與清除本身也寫稽核，而且清除類一律在刪除<b>之後</b>才寫，這筆才會留在清空後的表裡。</para>
/// <para>時間：資料庫存 UTC，畫面一律是伺服器本地時間；轉換集中在 <see cref="AuditLogQueryService"/>。</para>
/// </summary>
public partial class AuditLogView : ComponentBase
{
    private const string ResultSuccess = "success";
    private const string ResultFailed = "failed";

    /// <summary>CSV 欄位。來源 IP 放最後。</summary>
    public static readonly IReadOnlyList<CsvColumn<AuditLogAdapterModel>> CsvColumns =
    [
        new("發生時間（本地）", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("結果", x => x.SuccessText),
        new("動作名稱", x => x.ActionDisplayName),
        new("動作代碼", x => x.Action),
        new("操作者帳號", x => x.ActorAccount),
        new("操作者Id", x => x.ActorUserId?.ToString()),
        new("目標類型", x => x.TargetType),
        new("目標識別", x => x.TargetId),
        new("摘要", x => x.Detail),
        new("來源IP", x => x.ClientIp),
    ];

    [Inject]
    private AuditLogQueryService AuditLogQueryService { get; set; } = default!;

    [Inject]
    private IAuditLogService AuditLogService { get; set; } = default!;

    [Inject]
    private IOptionsMonitor<LogRetentionSettings> RetentionOptions { get; set; } = default!;

    [Inject]
    private FileDownloadInterop FileDownloadInterop { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private MessageService MessageService { get; set; } = default!;

    [Inject]
    private NotificationService NotificationService { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<AuditLogView> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private bool isAccessChecked;
    private bool isLoading;

    // ───────── 篩選（輸入中的值） ─────────
    private DateTime? startTime;
    private DateTime? endTime;
    private string actionFilter = string.Empty;
    private string resultFilter = string.Empty;
    private string actorAccount = string.Empty;
    private string clientIp = string.Empty;
    private string keyword = string.Empty;
    private IReadOnlyList<string> actionOptions = [];

    /// <summary>
    /// 按「查詢」當下凍結的條件：換頁、排序與匯出都用這一份，
    /// 輸入框改了但還沒按查詢時，畫面與匯出的內容才不會對不上（比照 TokenUsageView）。
    /// </summary>
    private AuditLogQuery appliedFilter = new();
    private bool? sortDescending;

    private List<AuditLogAdapterModel> rows = [];
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;

    // ───────── 明細 ─────────
    private bool detailVisible;
    private AuditLogAdapterModel? detail;

    private int PurgeDays => RetentionOptions.CurrentValue.ManualAuditLogDays;

    private string RetentionText => RetentionOptions.CurrentValue.AuditLogDays > 0
        ? $"超過 {RetentionOptions.CurrentValue.AuditLogDays} 天的紀錄每天自動清除。"
        : "目前不自動清除（LogRetentionSettings:AuditLogDays = 0）。";

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        // 只限管理者：頁面不在角色權限目錄裡。判定前不讀任何資料。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/audit-logs");
            Logger.LogWarning("Audit log view denied because the current user is not an administrator.");
            return;
        }

        appliedFilter = BuildFilter();
        await LoadActionOptionsAsync();
        await ReloadAsync();
    }

    // ───────── 查詢 ─────────

    private async Task LoadActionOptionsAsync()
    {
        try
        {
            actionOptions = await AuditLogQueryService.GetDistinctActionsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load audit log action options.");
        }
    }

    private async Task OnSearchAsync()
    {
        appliedFilter = BuildFilter();
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await LoadActionOptionsAsync();
        await ReloadAsync();
    }

    /// <summary>
    /// 重新載入目前頁。開頭的早退不可省：兩次載入重疊會讓同一個 circuit 的 DbContext 被並行使用。
    /// </summary>
    private async Task ReloadAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        StateHasChanged();

        try
        {
            var result = await AuditLogQueryService.GetAsync(BuildQuery(_pageIndex, _pageSize));
            rows = result.Result.ToList();
            _total = result.Count;

            // 清除後停在已不存在的頁：夾回最後一頁重載（比照 AiCallLogView）。
            if (rows.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                result = await AuditLogQueryService.GetAsync(BuildQuery(_pageIndex, _pageSize));
                rows = result.Result.ToList();
                _total = result.Count;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load audit logs.");
            _ = MessageService.ErrorAsync($"載入稽核紀錄失敗：{ex.Message}");
            rows = [];
            _total = 0;
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnTableChange(QueryModel<AuditLogAdapterModel> args)
    {
        _pageIndex = args.PageIndex;
        _pageSize = args.PageSize;

        var sort = args.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
        sortDescending = sort is null ? null : sort.SortDirection == SortDirection.Descending;

        await ReloadAsync();
    }

    /// <summary>把輸入框的值整理成查詢條件（按「查詢」時呼叫）。</summary>
    private AuditLogQuery BuildFilter() => new()
    {
        StartTime = startTime,
        EndTime = endTime,
        Action = actionFilter ?? string.Empty,
        Success = resultFilter switch
        {
            ResultSuccess => true,
            ResultFailed => false,
            _ => null,
        },
        ActorAccount = actorAccount ?? string.Empty,
        ClientIp = clientIp ?? string.Empty,
        Keyword = keyword ?? string.Empty,
    };

    private AuditLogQuery BuildQuery(int page, int pageSize) => new()
    {
        StartTime = appliedFilter.StartTime,
        EndTime = appliedFilter.EndTime,
        Action = appliedFilter.Action,
        Success = appliedFilter.Success,
        ActorAccount = appliedFilter.ActorAccount,
        ClientIp = appliedFilter.ClientIp,
        Keyword = appliedFilter.Keyword,
        CurrentPage = page,
        PageSize = pageSize,
        SortDescending = sortDescending,
    };

    private static string GetResultClass(AuditLogAdapterModel row)
        => row.Success ? "audit-log-pill audit-log-pill-ok" : "audit-log-pill audit-log-pill-err";

    /// <summary>動作膠囊依類別上色；類別對不上的一律用預設色。</summary>
    internal static string GetActionClass(AuditLogAdapterModel row) => row.ActionCategory switch
    {
        "Login" or "Logout" or "Token" or "Password" => "audit-log-pill audit-log-action-auth",
        "User" or "Role" or "Permission" => "audit-log-pill audit-log-action-account",
        "Audit" => "audit-log-pill audit-log-action-audit",
        "Ai" or "AiCallLog" or "AiUsage" or "AiChat" => "audit-log-pill audit-log-action-ai",
        "ExceptionLog" or "LogLevel" or "LogViewer" => "audit-log-pill audit-log-action-system",
        _ => "audit-log-pill",
    };

    // ───────── 明細 ─────────

    private void OnView(AuditLogAdapterModel row)
    {
        detail = row;
        detailVisible = true;
    }

    private void OnDetailCancel() => detailVisible = false;

    // ───────── 匯出、清除 ─────────

    private async Task OnExportAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        try
        {
            var items = await AuditLogQueryService.GetForExportAsync(BuildQuery(1, AuditLogQueryService.MaxExportRows));
            var bytes = CsvExport.ToCsv(CsvColumns, items);
            var fileName = $"MeetingRecord-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            await FileDownloadInterop.SaveBytesAsync(fileName, bytes, CsvExport.ContentType);

            await WriteSelfAuditAsync(AuditActions.AuditExport, $"format=csv; rows={items.Count}");
            NotifyDownloaded(fileName);
            if (items.Count >= AuditLogQueryService.MaxExportRows)
            {
                _ = MessageService.WarningAsync($"只匯出最新 {AuditLogQueryService.MaxExportRows:N0} 筆，請縮小查詢條件。");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Audit log export failed.");
            _ = MessageService.ErrorAsync($"匯出失敗：{ex.Message}");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnPurgeAsync()
    {
        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var days = PurgeDays;
        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = $"清除 {days} 天前的稽核紀錄",
            Content = $"將刪除 {days} 天前發生的所有稽核紀錄，無法復原。這次清除本身會留下一筆紀錄。",
            OkText = "清除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        var result = await AuditLogQueryService.PurgeAsync(days);
        if (result.Success)
        {
            await WriteSelfAuditAsync(AuditActions.AuditPurge, $"days={days}; {result.Message}");
            _ = MessageService.SuccessAsync(result.Message);
            _pageIndex = 1;
            await OnRefreshAsync();
        }
        else
        {
            _ = MessageService.ErrorAsync(result.Message);
        }
    }

    private async Task OnClearAllAsync()
    {
        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "清空全部稽核紀錄",
            Content = "將刪除所有稽核紀錄，無法復原。這次清空本身會留下一筆紀錄。",
            OkText = "全部清除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        var result = await AuditLogQueryService.ClearAllAsync();
        if (result.Success)
        {
            await WriteSelfAuditAsync(AuditActions.AuditClearAll, result.Message);
            _ = MessageService.SuccessAsync("已清空全部稽核紀錄");
            _pageIndex = 1;
            await OnRefreshAsync();
        }
        else
        {
            _ = MessageService.ErrorAsync(result.Message);
        }
    }

    private void NotifyDownloaded(string fileName)
        => _ = NotificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = $"已下載「{fileName}」。",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });

    /// <summary>本頁自己的稽核（匯出、清除）。⚠️ 清除類必須在刪除之後才呼叫。</summary>
    private Task WriteSelfAuditAsync(string action, string detailText)
    {
        var currentUser = CurrentUserService.CurrentUser;
        return AuditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: currentUser.Id > 0 ? currentUser.Id : null,
            actorAccount: currentUser.Id > 0 ? currentUser.Account : null,
            targetType: "AuditLog",
            targetId: "*",
            detail: detailText);
    }
}
