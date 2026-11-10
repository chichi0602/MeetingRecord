using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;

namespace MeetingRecord.Web.Client.Api;

/// <summary>分類清單（1.1.7）：<c>api/v1/Category</c>。規則都在伺服器（CategoryService），這裡只轉呼叫。</summary>
public sealed class CategoryApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/Category";

    public Task<ApiResult<PagedResult<CategoryDto>>> SearchAsync(CategorySearchRequestDto request)
        => api.PostAsync($"{BaseUrl}/search", request, ClientJsonContext.Default.CategorySearchRequestDto, ClientJsonContext.Default.CategoryPageResult);

    public Task<ApiResult<CategoryDto>> CreateAsync(CategoryCreateUpdateDto category)
        => api.PostAsync(BaseUrl, category, ClientJsonContext.Default.CategoryCreateUpdateDto, ClientJsonContext.Default.CategoryResult);

    public Task<ApiResult<object>> UpdateAsync(CategoryCreateUpdateDto category)
        => api.PutAsync($"{BaseUrl}/{category.Id}", category, ClientJsonContext.Default.CategoryCreateUpdateDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> SetEnabledAsync(int id, bool isEnabled)
        => api.PutAsync($"{BaseUrl}/{id}/enabled", new SetEnabledRequestDto { IsEnabled = isEnabled }, ClientJsonContext.Default.SetEnabledRequestDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<List<TeamOptionDto>>> GetSelectableTeamsAsync()
        => api.GetAsync($"{BaseUrl}/teams/selectable", ClientJsonContext.Default.TeamOptionsResult);
}
