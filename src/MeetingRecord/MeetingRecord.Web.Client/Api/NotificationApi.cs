using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>站內通知 API（1.1.2）：目前只有未讀數。</summary>
public sealed class NotificationApi(ApiClient api)
{
    public Task<ApiResult<int>> GetUnreadCountAsync()
        => api.GetAsync("api/v1/notifications/unread-count", ClientJsonContext.Default.IntResult);
}
