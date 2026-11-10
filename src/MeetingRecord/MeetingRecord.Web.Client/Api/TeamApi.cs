using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;

namespace MeetingRecord.Web.Client.Api;

/// <summary>團隊清單（1.1.8）：<c>api/v1/Team</c>。規則都在伺服器（TeamService），這裡只轉呼叫。</summary>
public sealed class TeamApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/Team";

    public Task<ApiResult<PagedResult<TeamDto>>> SearchAsync(TeamSearchRequestDto request)
        => api.PostAsync($"{BaseUrl}/search", request, ClientJsonContext.Default.TeamSearchRequestDto, ClientJsonContext.Default.TeamPageResult);

    /// <summary>單筆，含成員編號（清單沒有）。</summary>
    public Task<ApiResult<TeamDto>> GetByIdAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.TeamResult);

    public Task<ApiResult<TeamDto>> CreateAsync(TeamCreateUpdateDto team)
        => api.PostAsync(BaseUrl, team, ClientJsonContext.Default.TeamCreateUpdateDto, ClientJsonContext.Default.TeamResult);

    public Task<ApiResult<object>> UpdateAsync(TeamCreateUpdateDto team)
        => api.PutAsync($"{BaseUrl}/{team.Id}", team, ClientJsonContext.Default.TeamCreateUpdateDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    /// <summary>跳確認框之前先問能不能刪（還是專案主責團隊就不行，回 409 與原因）。</summary>
    public Task<ApiResult<object>> DeleteCheckAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}/delete-check", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> SetEnabledAsync(int id, bool isEnabled)
        => api.PutAsync($"{BaseUrl}/{id}/enabled", new SetEnabledRequestDto { IsEnabled = isEnabled }, ClientJsonContext.Default.SetEnabledRequestDto, ClientJsonContext.Default.EmptyResult);

    /// <summary>可加入的帳號（要有新增或修改團隊的權限）。</summary>
    public Task<ApiResult<List<MemberOptionDto>>> GetSelectableMembersAsync()
        => api.GetAsync($"{BaseUrl}/members/selectable", ClientJsonContext.Default.MemberOptionsResult);
}
