using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.AuditLogs;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.Admins;

/// <summary>
/// 稽核紀錄（1.0.32，移植自 NET10-Blazor-Starter 的 /audit-logs，畫面依本專案規範）。**只限管理者**。
///
/// <para>資料由伺服器的 <c>IAuditLogService</c> 在各業務流程寫入。本頁負責查詢、明細、匯出 CSV、清除 N 天前與全部清除，
/// 全部走 <c>api/v1/audit-logs</c>；刻意<b>沒有</b>單筆刪除。匯出與清除本身也寫稽核（伺服器寫，清除類在刪除<b>之後</b>）。</para>
/// <para>時間：資料庫存 UTC，畫面一律是伺服器本地時間；轉換集中在伺服器的 <c>AuditLogQueryService</c>。</para>
/// </summary>
public partial class AuditLogPage : ComponentBase
{
    private const string ResultSuccess = "success";
    private const string ResultFailed = "failed";

    [Inject] private AuditLogApi AuditLogApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private FileDownloadInterop FileDownloadInterop { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private MessageService MessageService { get; set; } = default!;
    [Inject] private NotificationService NotificationService { get; set; } = default!;
    [Inject] private ILogger<AuditLogPage> Logger { get; set; } = default!;

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
    /// 輸入框改了但還沒按查詢時，畫面與匯出的內容才不會對不上（比照 Token 用量）。
    /// </summary>
    private AuditLogQuery appliedFilter = new();
    private bool? sortDescending;

    private List<AuditLogAdapterModel> rows = [];
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;

    private AuditLogMetaDto? meta;

    // ───────── 明細 ─────────
    private bool detailVisible;
    private AuditLogAdapterModel? detail;

    private int PurgeDays => meta?.PurgeDays ?? 365;

    private int MaxExportRows => meta?.MaxExportRows ?? 10_000;

    private string RetentionText => meta is null
        ? string.Empty
        : meta.RetentionDays > 0
            ? $"超過 {meta.RetentionDays} 天的紀錄每天自動清除。"
            : "目前不自動清除（LogRetentionSettings:AuditLogDays = 0）。";

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        isAccessChecked = true;

        // 只限管理者：頁面不在角色權限目錄裡。判定前不讀任何資料。API 也是 [AdminOnly]。
        if (!PermissionView.IsAdmin)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/audit-logs");
            Logger.LogWarning("Audit log view denied because the current user is not an administrator.");
            return;
        }

        var metaResult = await AuditLogApi.GetMetaAsync();
        if (metaResult.Success)
        {
            meta = metaResult.Data;
        }
        else
        {
            Logger.LogWarning("Failed to load audit log settings: {Message}", ApiClient.ErrorText(metaResult));
        }

        appliedFilter = BuildFilter();
        await LoadActionOptionsAsync();
        await ReloadAsync();
    }

    // ───────── 查詢 ─────────

    private async Task LoadActionOptionsAsync()
    {
        var result = await AuditLogApi.GetActionsAsync();
        if (result.Success && result.Data is { } actions)
        {
            actionOptions = actions;
        }
        else
        {
            Logger.LogWarning("Failed to load audit log action options: {Message}", ApiClient.ErrorText(result));
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

    /// <summary>重新載入目前頁。重疊時忽略。</summary>
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
            var result = await AuditLogApi.SearchAsync(BuildQuery(_pageIndex, _pageSize));
            if (!result.Success || result.Data is not { } page)
            {
                _ = MessageService.ErrorAsync($"載入稽核紀錄失敗：{ApiClient.ErrorText(result)}");
                rows = [];
                _total = 0;
                return;
            }

            rows = page.Items.ToList();
            _total = page.TotalCount;

            // 清除後停在已不存在的頁：夾回最後一頁重載（比照 AI 對話紀錄）。
            if (rows.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                result = await AuditLogApi.SearchAsync(BuildQuery(_pageIndex, _pageSize));
                rows = result.Data?.Items.ToList() ?? [];
                _total = result.Data?.TotalCount ?? 0;
            }
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
        "Project" or "Meeting" or "Todo" => "audit-log-pill audit-log-action-business",
        "Team" or "Category" or "PromptTemplate" => "audit-log-pill audit-log-action-definition",
        _ => "audit-log-pill",
    };

    // ───────── 明細 ─────────

    private void OnView(AuditLogAdapterModel row)
    {
        detail = row;
        detailVisible = true;
    }

    private void OnDetailCancel() => detailVisible = false;

    // ───────── 匯出、清除（伺服器執行、伺服器寫稽核） ─────────

    private async Task OnExportAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        try
        {
            var download = await AuditLogApi.ExportCsvAsync(BuildQuery(1, MaxExportRows));
            if (!download.Success || download.Content is null)
            {
                _ = MessageService.ErrorAsync($"匯出失敗：{download.Error}");
                return;
            }

            await FileDownloadInterop.SaveBytesAsync(download.FileName!, download.Content, download.ContentType!);
            NotifyDownloaded(download.FileName!);
            if (_total >= MaxExportRows)
            {
                _ = MessageService.WarningAsync($"只匯出最新 {MaxExportRows:N0} 筆，請縮小查詢條件。");
            }
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnPurgeAsync()
    {
        if (isLoading)
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

        var result = await AuditLogApi.PurgeAsync();
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
            return;
        }

        _ = MessageService.SuccessAsync(result.Message);
        _pageIndex = 1;
        await OnRefreshAsync();
    }

    private async Task OnClearAllAsync()
    {
        if (isLoading)
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

        var result = await AuditLogApi.ClearAllAsync();
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
            return;
        }

        _ = MessageService.SuccessAsync("已清空全部稽核紀錄");
        _pageIndex = 1;
        await OnRefreshAsync();
    }

    private void NotifyDownloaded(string fileName)
        => _ = NotificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = $"已下載「{fileName}」。",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });
}
