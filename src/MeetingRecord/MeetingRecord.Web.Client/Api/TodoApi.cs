using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Todos;

namespace MeetingRecord.Web.Client.Api;

/// <summary>待辦事項（1.1.19）：<c>api/v1/Todo</c>。規則都在伺服器（TodoService），這裡只轉呼叫。</summary>
public sealed class TodoApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/Todo";

    public Task<ApiResult<PagedResult<TodoDto>>> TreeAsync(TodoTreeRequestDto request)
        => api.PostAsync($"{BaseUrl}/tree", request, ClientJsonContext.Default.TodoTreeRequestDto, ClientJsonContext.Default.TodoPageResult);

    public Task<ApiResult<TodoDto>> GetAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.TodoResult);

    /// <summary>新增：一律由伺服器配號（送出前把編號歸零）。</summary>
    public Task<ApiResult<TodoDto>> CreateAsync(TodoCreateUpdateDto todo)
        => api.PostAsync(BaseUrl, todo, ClientJsonContext.Default.TodoCreateUpdateDto, ClientJsonContext.Default.TodoResult);

    public Task<ApiResult<object>> UpdateAsync(TodoCreateUpdateDto todo)
        => api.PutAsync($"{BaseUrl}/{todo.Id}", todo, ClientJsonContext.Default.TodoCreateUpdateDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> SetCompletedAsync(int id, bool isCompleted)
        => api.PutAsync($"{BaseUrl}/{id}/completed", new TodoCompletedRequestDto { IsCompleted = isCompleted }, ClientJsonContext.Default.TodoCompletedRequestDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<List<TodoOwnerSummaryDto>>> GetOwnersAsync(int? projectId, string scope)
        => api.GetAsync($"{BaseUrl}/owners?{ProjectQuery(projectId)}scope={scope}", ClientJsonContext.Default.TodoOwnersResult);

    public Task<ApiResult<List<TodoDto>>> GetByOwnerAsync(string owner, int? projectId, string scope)
        => api.GetAsync($"{BaseUrl}/by-owner?owner={Uri.EscapeDataString(owner)}&{ProjectQuery(projectId)}scope={scope}", ClientJsonContext.Default.TodoListResult);

    public Task<ApiResult<List<TodoChangeDto>>> GetChangesAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}/changes", ClientJsonContext.Default.TodoChangesResult);

    public Task<ApiResult<List<TodoParentOptionDto>>> GetParentCandidatesAsync(int projectId, int? excludeId)
        => api.GetAsync($"{BaseUrl}/parent-candidates?projectId={projectId}" + (excludeId is { } id ? $"&excludeId={id}" : string.Empty), ClientJsonContext.Default.TodoParentOptionsResult);

    public Task<ApiResult<List<TodoProjectOptionDto>>> GetSelectableProjectsAsync()
        => api.GetAsync($"{BaseUrl}/projects/selectable", ClientJsonContext.Default.TodoProjectOptionsResult);

    /// <summary>
    /// 失敗原因：新增、修改、刪除沿用既有對外 API 的格式（放在 <c>errors.Validation</c>），新端點放 <c>errorMessage</c>，兩種都認。
    /// </summary>
    public static string ErrorText<T>(ApiResult<T> result) => ApiClient.ErrorText(result);

    private static string ProjectQuery(int? projectId) => projectId is > 0 ? $"projectId={projectId}&" : string.Empty;
}
