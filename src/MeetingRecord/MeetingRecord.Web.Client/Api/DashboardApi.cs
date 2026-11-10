using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Dashboards;

namespace MeetingRecord.Web.Client.Api;

/// <summary>儀表板（1.1.12）：<c>api/v1/dashboard</c>，登入即可看；看不到的專案名稱由伺服器改成「其他專案」。</summary>
public sealed class DashboardApi(ApiClient api)
{
    public Task<ApiResult<DashboardSummary>> GetAsync()
        => api.GetAsync("api/v1/dashboard", ClientJsonContext.Default.DashboardResult);
}
