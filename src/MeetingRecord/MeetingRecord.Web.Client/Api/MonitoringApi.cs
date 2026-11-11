using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;

namespace MeetingRecord.Web.Client.Api;

/// <summary>監控與診斷（WASM 遷移 M5）：<c>api/v1/monitoring</c>，只限管理者。</summary>
public sealed class MonitoringApi(ApiClient api)
{
    private const string Base = "api/v1/monitoring";

    /// <summary>資料庫用量（1.1.32）。</summary>
    public Task<ApiResult<DatabaseUsageReport>> GetDatabaseUsageAsync()
        => api.GetAsync($"{Base}/database-usage", ClientJsonContext.Default.DatabaseUsageResult);

    /// <summary>檔案儲存用量（1.1.32）：影音目錄可能很大，和資料庫用量分開讀。</summary>
    public Task<ApiResult<StorageUsageReport>> GetStorageUsageAsync()
        => api.GetAsync($"{Base}/storage-usage", ClientJsonContext.Default.StorageUsageResult);
}
