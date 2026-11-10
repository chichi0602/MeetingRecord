using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Users;

namespace MeetingRecord.Web.Client.Api;

/// <summary>使用者管理（1.1.10，只限管理者）：<c>api/v1/User</c>。規則都在伺服器（MyUserService），這裡只轉呼叫。</summary>
public sealed class UserApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/User";

    public Task<ApiResult<PagedResult<UserDto>>> SearchAsync(UserSearchRequestDto request)
        => api.PostAsync($"{BaseUrl}/search", request, ClientJsonContext.Default.UserSearchRequestDto, ClientJsonContext.Default.UserPageResult);

    /// <summary>單筆，含額外角色與團隊（清單沒有額外角色）。</summary>
    public Task<ApiResult<UserDto>> GetByIdAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.UserResult);

    public Task<ApiResult<UserDto>> CreateAsync(UserCreateUpdateDto user)
        => api.PostAsync(BaseUrl, user, ClientJsonContext.Default.UserCreateUpdateDto, ClientJsonContext.Default.UserResult);

    public Task<ApiResult<object>> UpdateAsync(UserCreateUpdateDto user)
        => api.PutAsync($"{BaseUrl}/{user.Id}", user, ClientJsonContext.Default.UserCreateUpdateDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> SetStatusAsync(int id, bool isEnabled)
        => api.PutAsync($"{BaseUrl}/{id}/status", new SetEnabledRequestDto { IsEnabled = isEnabled }, ClientJsonContext.Default.SetEnabledRequestDto, ClientJsonContext.Default.EmptyResult);

    /// <summary>批次加入或移出團隊，回傳實際異動人數。</summary>
    public Task<ApiResult<int>> BatchTeamAsync(List<int> userIds, int teamId, bool add)
        => api.PostAsync($"{BaseUrl}/teams/batch", new UserTeamBatchRequestDto { UserIds = userIds, TeamId = teamId, Add = add }, ClientJsonContext.Default.UserTeamBatchRequestDto, ClientJsonContext.Default.IntResult);

    public Task<ApiResult<List<RoleOptionDto>>> GetRolesAsync()
        => api.GetAsync($"{BaseUrl}/roles", ClientJsonContext.Default.RoleOptionsResult);

    public Task<ApiResult<List<TeamOptionDto>>> GetTeamsAsync()
        => api.GetAsync($"{BaseUrl}/teams", ClientJsonContext.Default.TeamOptionsResult);
}
