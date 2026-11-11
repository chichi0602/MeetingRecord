using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;

namespace MeetingRecord.Web.Client.Pages.Monitoring;

/// <summary>
/// 資料庫用量（0.4.120；1.1.32 從 Blazor Server 的 DatabaseUsageView 搬到 WASM，畫面不變）。**管理者限定**、唯讀。
///
/// <para>上半部是 SQLite，下半部「檔案儲存用量」是資料庫外的影音、逐字稿與附件——本系統真正吃空間的地方。
/// 兩者都在伺服器量（<c>api/v1/monitoring/database-usage</c>、<c>storage-usage</c>），這裡只顯示。</para>
/// </summary>
public partial class DatabaseUsagePage : ComponentBase
{
    /// <summary>磁碟可用空間低於此值時標紅，與系統健康監控「主機資源」的門檻一致。</summary>
    private const long LowDiskThresholdBytes = 1024L * 1024 * 1024;

    [Inject] private MonitoringApi MonitoringApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private ILogger<DatabaseUsagePage> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private DatabaseUsageReport? report;
    private StorageUsageReport? storage;
    private bool isAccessChecked;
    private bool isLoading;

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        isAccessChecked = true;

        // 管理者限定：權限鍵刻意不在角色權限目錄裡，所以直接看是不是管理者（API 也是 [AdminOnly]）。
        if (!PermissionView.IsAdmin)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/database-usage");
            Logger.LogWarning("Database usage page denied because the current user is not an administrator.");
            return;
        }

        await ReloadAsync();
    }

    /// <summary>先量資料庫（很快）並立刻畫出來，再等外部目錄（影音目錄可能很大）。重複按「重新整理」時忽略。</summary>
    private async Task ReloadAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        storage = null;
        StateHasChanged();

        try
        {
            var database = await MonitoringApi.GetDatabaseUsageAsync();
            report = database.Success && database.Data is { } data
                ? data
                : new DatabaseUsageReport { ErrorMessage = ApiClient.ErrorText(database) };
            StateHasChanged();

            var files = await MonitoringApi.GetStorageUsageAsync();
            storage = files.Success ? files.Data : null;
            if (!files.Success)
            {
                Logger.LogWarning("Loading storage usage failed: {Message}", ApiClient.ErrorText(files));
            }
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private static string DescribeOnDiskFiles(DatabaseUsageReport report)
    {
        var parts = new List<string> { "主檔" };
        if (report.WalFileExists)
        {
            parts.Add($"WAL {FileSizeFormatter.Describe(report.WalBytes)}");
        }

        if (report.ShmFileExists)
        {
            parts.Add($"SHM {FileSizeFormatter.Describe(report.ShmBytes)}");
        }

        return parts.Count == 1 ? "只有主檔（未使用 WAL）" : string.Join(" + ", parts);
    }
}
