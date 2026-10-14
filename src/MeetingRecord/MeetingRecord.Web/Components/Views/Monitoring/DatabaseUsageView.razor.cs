using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.Components.Views.Monitoring;

/// <summary>
/// 資料庫用量（0.4.120，移植自 NET10-Blazor-Starter）。**管理者限定**、唯讀。
///
/// <para>上半部是 SQLite（<see cref="IDatabaseUsageService"/>）；下半部「檔案儲存用量」是本專案加的
/// （<see cref="StorageUsageService"/>）——本系統真正吃空間的是資料庫外的影音、逐字稿與附件。</para>
/// </summary>
public partial class DatabaseUsageView : ComponentBase
{
    /// <summary>磁碟可用空間低於此值時標紅，與系統健康監控「主機資源」的門檻一致。</summary>
    private const long LowDiskThresholdBytes = 1024L * 1024 * 1024;

    [Inject]
    private IDatabaseUsageService DatabaseUsageService { get; set; } = default!;

    [Inject]
    private StorageUsageService StorageUsageService { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<DatabaseUsageView> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private DatabaseUsageReport? report;
    private StorageUsageReport? storage;
    private bool isAccessChecked;
    private bool isLoading;

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        // 管理者限定：權限鍵刻意不在角色權限目錄裡，所以直接看 CheckIsAdmin。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Database usage page denied because the current user is not an administrator.");
            return;
        }

        await ReloadAsync();
    }

    /// <summary>
    /// 先量資料庫（很快）並立刻畫出來，再到背景執行緒量外部目錄（影音目錄可能很大）。
    /// 開頭的早退不可省：兩個 ReloadAsync 重疊會讓同一個 circuit 的 DbContext 被並行使用。
    /// </summary>
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
            report = await DatabaseUsageService.GetReportAsync();
            StateHasChanged();

            storage = await Task.Run(StorageUsageService.GetReport);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading database usage failed.");
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
