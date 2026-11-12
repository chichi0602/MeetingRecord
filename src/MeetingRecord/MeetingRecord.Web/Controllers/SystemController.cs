using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Health;

namespace MeetingRecord.Web.Controllers;

/// <summary>系統資訊（1.1.6）：「關於」對話窗的內容。只需登入——所有登入者都看得到。</summary>
[Route("api/v1/system")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class SystemController(SystemAboutProvider aboutProvider) : ControllerBase
{
    [HttpGet("about")]
    public ActionResult<ApiResult<SystemAboutDto>> GetAbout()
        => Ok(ApiResult<SystemAboutDto>.SuccessResult(aboutProvider.Get()));
}
