using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Roles;

namespace MeetingRecord.Web.Client.Api;

/// <summary>角色管理（1.1.11，只限管理者）：<c>api/v1/Role</c>。權限樹轉權限鍵、目錄檢查、刪除時的重新指派都在伺服器。</summary>
public sealed class RoleApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/Role";

    /// <summary>清單（不帶權限樹）。</summary>
    public Task<ApiResult<PagedResult<RoleDto>>> SearchAsync(RoleSearchRequestDto request)
        => api.PostAsync($"{BaseUrl}/search", request, ClientJsonContext.Default.RoleSearchRequestDto, ClientJsonContext.Default.RolePageResult);

    /// <summary>單筆，含權限樹。</summary>
    public Task<ApiResult<RoleDto>> GetByIdAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.RoleResult);

    /// <summary>全部未勾選的權限樹（新增用）。</summary>
    public Task<ApiResult<RolePermissionDto>> GetPermissionTemplateAsync()
        => api.GetAsync($"{BaseUrl}/permission-template", ClientJsonContext.Default.RolePermissionResult);

    public Task<ApiResult<RoleDto>> CreateAsync(RoleDto role)
        => api.PostAsync(BaseUrl, role, ClientJsonContext.Default.RoleDto, ClientJsonContext.Default.RoleResult);

    public Task<ApiResult<object>> UpdateAsync(RoleDto role)
        => api.PutAsync($"{BaseUrl}/{role.Id}", role, ClientJsonContext.Default.RoleDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    /// <summary>掛著這個角色的人數（刪除確認框用）。</summary>
    public Task<ApiResult<int>> GetUserCountAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}/user-count", ClientJsonContext.Default.IntResult);

    /// <summary>建立預設角色；成功時 <c>Message</c> 說明新增與略過幾個。</summary>
    public Task<ApiResult<object>> AddPresetsAsync()
        => api.PostAsync($"{BaseUrl}/presets", ClientJsonContext.Default.EmptyResult);
}
