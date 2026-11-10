using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Projects;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// 專案項目（1.1.26）：<c>api/v1/Project</c>（專案本身，1.1.24）與 <c>api/v1/Project/{id}/…</c>（專案底下的會議，1.1.25）。
/// 規則都在伺服器（ProjectService、MeetingService），這裡只轉呼叫。權限一律看專案項目。
/// </summary>
public sealed class ProjectApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/Project";

    // ---------------- 專案本身 ----------------

    public Task<ApiResult<List<ProjectDto>>> GetSelectableAsync(bool includeArchived)
        => api.GetAsync($"{BaseUrl}/selectable?includeArchived={(includeArchived ? "true" : "false")}", ClientJsonContext.Default.ProjectListResult);

    /// <summary>單筆（含附件與呼叫者的權限旗標）。</summary>
    public Task<ApiResult<ProjectDto>> GetAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.ProjectResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<LastSelectedProjectDto>> GetLastSelectedAsync()
        => api.GetAsync($"{BaseUrl}/last-selected", ClientJsonContext.Default.LastSelectedProjectResult);

    public Task<ApiResult<object>> SaveLastSelectedAsync(int projectId)
        => api.PutAsync($"{BaseUrl}/last-selected", new LastSelectedProjectDto { ProjectId = projectId }, ClientJsonContext.Default.LastSelectedProjectDto, ClientJsonContext.Default.EmptyResult);

    /// <summary>啟用中的分類名稱（篩選與表單用，只要專案項目檢視權限）。</summary>
    public Task<ApiResult<List<string>>> GetEnabledCategoriesAsync()
        => api.GetAsync($"{BaseUrl}/categories/enabled", ClientJsonContext.Default.StringListResult);

    /// <summary>團隊篩選選項（全部啟用中的團隊，只要專案項目檢視權限）。</summary>
    public Task<ApiResult<List<TeamOptionDto>>> GetFilterTeamsAsync()
        => api.GetAsync($"{BaseUrl}/teams/filter", ClientJsonContext.Default.TeamOptionsResult);

    public Task<ApiResult<List<TeamOptionDto>>> GetPrimaryTeamsAsync()
        => api.GetAsync($"{BaseUrl}/teams/primary", ClientJsonContext.Default.TeamOptionsResult);

    /// <summary>協作團隊選項；<paramref name="include"/>＝專案已掛著的團隊（可能已停用）也列進來。</summary>
    public Task<ApiResult<List<TeamOptionDto>>> GetCollaboratorTeamsAsync(IEnumerable<int> include)
        => api.GetAsync($"{BaseUrl}/teams/collaborator{Query("include", include)}", ClientJsonContext.Default.TeamOptionsResult);

    public Task<ApiResult<List<ProjectOwnerCandidateDto>>> GetOwnerCandidatesAsync(int? primaryTeamId, IEnumerable<int> collaboratorTeamIds)
    {
        var query = Query("collaboratorTeamIds", collaboratorTeamIds);
        if (primaryTeamId is { } primary)
        {
            query += (query.Length == 0 ? "?" : "&") + $"primaryTeamId={primary}";
        }

        return api.GetAsync($"{BaseUrl}/owner-candidates{query}", ClientJsonContext.Default.ProjectOwnerCandidatesResult);
    }

    /// <summary>新增（整筆、可帶附件）。附件存不進去時回失敗、但 <c>Data.ProjectId</c> 有值：畫面要切成修改模式重送。</summary>
    public Task<ApiResult<ProjectSaveResultDto>> CreateAsync(ProjectFormDto form)
        => api.PostAsync($"{BaseUrl}/form", form, ClientJsonContext.Default.ProjectFormDto, ClientJsonContext.Default.ProjectSaveResult);

    /// <summary>修改（整筆覆寫，可加、移除附件）。</summary>
    public Task<ApiResult<ProjectSaveResultDto>> UpdateAsync(ProjectFormDto form)
        => api.PutAsync($"{BaseUrl}/{form.Id}/form", form, ClientJsonContext.Default.ProjectFormDto, ClientJsonContext.Default.ProjectSaveResult);

    /// <summary>下載附件（伺服器寫稽核）。</summary>
    public Task<ApiDownload> DownloadFileAsync(int fileId)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/files/{fileId}"));

    // ---------------- 專案底下的會議（1.1.25） ----------------

    public Task<ApiResult<List<MeetingDto>>> GetMeetingsAsync(int projectId)
        => api.GetAsync($"{BaseUrl}/{projectId}/meetings", ClientJsonContext.Default.MeetingListResult);

    /// <summary>單一會議（含會議紀錄全文）。</summary>
    public Task<ApiResult<MeetingDto>> GetMeetingAsync(int projectId, int meetingId)
        => api.GetAsync($"{BaseUrl}/{projectId}/meetings/{meetingId}", ClientJsonContext.Default.MeetingResult);

    public Task<ApiResult<List<ProjectTranscriptOptionDto>>> GetSelectableTranscriptsAsync(int projectId)
        => api.GetAsync($"{BaseUrl}/{projectId}/transcripts/selectable", ClientJsonContext.Default.ProjectTranscriptOptionsResult);

    public Task<ApiResult<List<MeetingPromptOptionDto>>> GetPromptTemplatesAsync(int projectId)
        => api.GetAsync($"{BaseUrl}/{projectId}/prompt-templates/selectable", ClientJsonContext.Default.MeetingPromptOptionsResult);

    /// <summary>產生（或重新產生）會議紀錄並歸屬到這個專案；會花 AI 費用。</summary>
    public Task<ApiResult<object>> RequestDraftAsync(int projectId, int meetingId, MeetingDraftRequestDto request)
        => api.PostAsync($"{BaseUrl}/{projectId}/meetings/{meetingId}/draft", request, ClientJsonContext.Default.MeetingDraftRequestDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> UpdateDraftAsync(int projectId, int meetingId, string content)
        => api.PutAsync($"{BaseUrl}/{projectId}/meetings/{meetingId}/draft", new MeetingDraftContentDto { Content = content }, ClientJsonContext.Default.MeetingDraftContentDto, ClientJsonContext.Default.EmptyResult);

    /// <summary>從專案移除（只限上傳者，伺服器把關）。</summary>
    public Task<ApiResult<object>> DetachAsync(int projectId, int meetingId)
        => api.DeleteAsync($"{BaseUrl}/{projectId}/meetings/{meetingId}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<MeetingTranscriptDto>> GetTranscriptAsync(int projectId, int meetingId)
        => api.GetAsync($"{BaseUrl}/{projectId}/meetings/{meetingId}/transcript", ClientJsonContext.Default.MeetingTranscriptResult);

    public Task<ApiDownload> DownloadTranscriptAsync(int projectId, int meetingId)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{projectId}/meetings/{meetingId}/transcript/file"));

    public Task<ApiResult<object>> TranscriptCopiedAsync(int projectId, int meetingId)
        => api.PostAsync($"{BaseUrl}/{projectId}/meetings/{meetingId}/transcript/copied", ClientJsonContext.Default.EmptyResult);

    /// <summary>會議紀錄 PDF（表頭帶專案名稱）；帶 <paramref name="content"/> 就用它（編修中、還沒存的內容）。</summary>
    public Task<ApiDownload> DownloadDraftPdfAsync(int projectId, int meetingId, string? content)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{projectId}/meetings/{meetingId}/draft/pdf")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new MeetingPdfRequestDto { Content = content }, ClientJsonContext.Default.MeetingPdfRequestDto),
        });

    /// <summary>專案全部待辦的 PDF 總覽。</summary>
    public Task<ApiDownload> DownloadTodosPdfAsync(int projectId)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{projectId}/todos/pdf"));

    private static string Query(string name, IEnumerable<int> values)
    {
        var parts = values.Select(x => $"{name}={x}").ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
