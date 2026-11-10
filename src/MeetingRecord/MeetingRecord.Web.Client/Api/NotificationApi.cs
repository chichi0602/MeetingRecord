using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Notifications;

namespace MeetingRecord.Web.Client.Api;

/// <summary>站內通知 API（1.1.2 未讀數；1.1.6 清單與標已讀）。</summary>
public sealed class NotificationApi(ApiClient api)
{
    public Task<ApiResult<int>> GetUnreadCountAsync()
        => api.GetAsync("api/v1/notifications/unread-count", ClientJsonContext.Default.IntResult);

    public Task<ApiResult<List<NotificationDto>>> GetRecentAsync()
        => api.GetAsync("api/v1/notifications", ClientJsonContext.Default.NotificationsResult);

    public Task<ApiResult<object>> MarkReadAsync(int id)
        => api.PostAsync($"api/v1/notifications/{id}/read", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> MarkAllReadAsync()
        => api.PostAsync("api/v1/notifications/read-all", ClientJsonContext.Default.EmptyResult);
}
