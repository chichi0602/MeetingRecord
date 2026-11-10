using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Todos;

namespace MeetingRecord.Web.Client.Api;

/// <summary>從會議紀錄抽出待辦（1.1.26）：<c>api/v1/todo-extraction</c>（1.1.25）。都要「待辦事項」新增權限。</summary>
public sealed class TodoExtractionApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/todo-extraction";

    /// <summary>抽出候選（會花 AI 費用），連同先前已加入過的標題與本專案未完成的待辦。</summary>
    public Task<ApiResult<TodoExtractionResultDto>> ExtractAsync(int meetingId)
        => api.PostAsync($"{BaseUrl}/meetings/{meetingId}", ClientJsonContext.Default.TodoExtractionResult);

    /// <summary>加為新待辦，回新待辦的編號。</summary>
    public Task<ApiResult<int>> AddAsync(ExtractedTodoAddRequestDto request)
        => api.PostAsync($"{BaseUrl}/todos", request, ClientJsonContext.Default.ExtractedTodoAddRequestDto, ClientJsonContext.Default.IntResult);

    /// <summary>用候選更新既有待辦（只改期限與負責人）。</summary>
    public Task<ApiResult<object>> ApplyUpdateAsync(int todoId, ExtractedTodoUpdateRequestDto request)
        => api.PutAsync($"{BaseUrl}/todos/{todoId}", request, ClientJsonContext.Default.ExtractedTodoUpdateRequestDto, ClientJsonContext.Default.EmptyResult);
}
