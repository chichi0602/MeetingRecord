using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Systems;

namespace MeetingRecord.Web.Client.Api;

/// <summary>系統資訊（1.1.6）：「關於」對話窗。</summary>
public sealed class SystemApi(ApiClient api)
{
    public Task<ApiResult<SystemAboutDto>> GetAboutAsync()
        => api.GetAsync("api/v1/system/about", ClientJsonContext.Default.AboutResult);
}
