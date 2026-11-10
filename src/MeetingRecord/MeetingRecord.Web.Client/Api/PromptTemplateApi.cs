using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;

namespace MeetingRecord.Web.Client.Api;

/// <summary>會議紀錄提示詞清單（1.1.9）：<c>api/v1/PromptTemplate</c>。規則都在伺服器（PromptTemplateService），這裡只轉呼叫。</summary>
public sealed class PromptTemplateApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/PromptTemplate";

    public Task<ApiResult<PagedResult<PromptTemplateDto>>> SearchAsync(PromptTemplateSearchRequestDto request)
        => api.PostAsync($"{BaseUrl}/search", request, ClientJsonContext.Default.PromptTemplateSearchRequestDto, ClientJsonContext.Default.PromptTemplatePageResult);

    public Task<ApiResult<PromptTemplateDto>> CreateAsync(PromptTemplateCreateUpdateDto template)
        => api.PostAsync(BaseUrl, template, ClientJsonContext.Default.PromptTemplateCreateUpdateDto, ClientJsonContext.Default.PromptTemplateResult);

    public Task<ApiResult<object>> UpdateAsync(PromptTemplateCreateUpdateDto template)
        => api.PutAsync($"{BaseUrl}/{template.Id}", template, ClientJsonContext.Default.PromptTemplateCreateUpdateDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> SetEnabledAsync(int id, bool isEnabled)
        => api.PutAsync($"{BaseUrl}/{id}/enabled", new SetEnabledRequestDto { IsEnabled = isEnabled }, ClientJsonContext.Default.SetEnabledRequestDto, ClientJsonContext.Default.EmptyResult);

    /// <summary>內建範本名稱（要能新增提示詞）。</summary>
    public Task<ApiResult<List<string>>> GetPresetNamesAsync()
        => api.GetAsync($"{BaseUrl}/presets", ClientJsonContext.Default.StringListResult);

    /// <summary>一次建立全部內建範本；成功時 <c>Message</c> 說明新增與略過幾筆。</summary>
    public Task<ApiResult<object>> AddAllPresetsAsync()
        => api.PostAsync($"{BaseUrl}/presets", ClientJsonContext.Default.EmptyResult);

    /// <summary>只新增一個內建範本；已存在回 409「「x」已存在，未新增。」。</summary>
    public Task<ApiResult<PromptTemplateDto>> AddPresetAsync(string name)
        => api.PostAsync($"{BaseUrl}/presets/single", new PresetNameRequestDto { Name = name }, ClientJsonContext.Default.PresetNameRequestDto, ClientJsonContext.Default.PromptTemplateResult);

    /// <summary>表單「分類」下拉的選項：啟用中的分類名稱。</summary>
    public Task<ApiResult<List<string>>> GetEnabledCategoriesAsync()
        => api.GetAsync($"{BaseUrl}/categories/enabled", ClientJsonContext.Default.StringListResult);
}
