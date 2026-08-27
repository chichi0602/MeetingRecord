using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Repositories;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 會議紀錄的 Web API（僅中繼資料的 CRUD）。
///
/// 影音檔上傳、語音轉錄與逐字稿讀取刻意不開放 API：那些操作牽涉實體檔案、
/// 背景佇列與長時間外部呼叫，只在 Blazor 服務層提供（見 docs/prd/會議紀錄-prd.md）。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class MeetingController : ControllerBase
{
    private readonly ILogger<MeetingController> logger;
    private readonly MeetingRepository meetingRepository;
    private readonly MeetingFileStore meetingFileStore;
    private readonly IMapper mapper;

    public MeetingController(
        ILogger<MeetingController> logger,
        MeetingRepository meetingRepository,
        MeetingFileStore meetingFileStore,
        IMapper mapper)
    {
        this.logger = logger;
        this.meetingRepository = meetingRepository;
        this.meetingFileStore = meetingFileStore;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<MeetingDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received meeting get request. MeetingId={MeetingId}", id);

            var meeting = await meetingRepository.GetByIdAsync(id);
            if (meeting == null)
            {
                logger.LogWarning("Meeting get request could not find record. MeetingId={MeetingId}", id);
                return NotFound(ApiResult<MeetingDto>.NotFoundResult($"找不到 ID 為 {id} 的會議紀錄"));
            }

            var meetingDto = mapper.Map<MeetingDto>(meeting);
            logger.LogInformation("Meeting retrieved successfully. MeetingId={MeetingId}", id);
            return Ok(ApiResult<MeetingDto>.SuccessResult(meetingDto, "取得會議紀錄成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get meeting. MeetingId={MeetingId}", id);
            return this.ApiServerError<MeetingDto>("取得會議紀錄失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<MeetingDto>>>> Search([FromBody] MeetingSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received meeting search request. Keyword={Keyword}, TranscriptionStatus={TranscriptionStatus}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}",
                request.Keyword,
                request.TranscriptionStatus,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending);

            var pagedResult = await meetingRepository.GetPagedAsync(request);
            var meetingDtos = mapper.Map<List<MeetingDto>>(pagedResult.Items);

            var result = new PagedResult<MeetingDto>
            {
                Items = meetingDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Meeting search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}",
                result.Items.Count,
                result.TotalCount);

            return Ok(ApiResult<PagedResult<MeetingDto>>.SuccessResult(result, "搜尋會議紀錄成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search meetings");
            return this.ApiServerError<PagedResult<MeetingDto>>("搜尋會議紀錄失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<MeetingDto>>> Create([FromBody] MeetingCreateUpdateDto meetingDto)
    {
        try
        {
            logger.LogDebug("Received meeting create request. Title={Title}", meetingDto.Title);

            var meeting = mapper.Map<Meeting>(meetingDto);
            var created = await meetingRepository.AddAsync(meeting);
            var createdDto = mapper.Map<MeetingDto>(created);

            logger.LogInformation("Meeting created successfully. MeetingId={MeetingId}, Title={Title}", createdDto.Id, createdDto.Title);
            return Ok(ApiResult<MeetingDto>.SuccessResult(createdDto, "新增會議紀錄成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create meeting. Title={Title}", meetingDto.Title);
            return this.ApiServerError<MeetingDto>("新增會議紀錄失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] MeetingCreateUpdateDto meetingDto)
    {
        try
        {
            logger.LogDebug("Received meeting update request. RouteId={RouteId}, PayloadId={PayloadId}, Title={Title}", id, meetingDto.Id, meetingDto.Title);

            if (id != meetingDto.Id)
            {
                logger.LogWarning("Meeting update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, meetingDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            var meeting = mapper.Map<Meeting>(meetingDto);
            var success = await meetingRepository.UpdateAsync(meeting);
            if (!success)
            {
                logger.LogWarning("Meeting update request could not find record. MeetingId={MeetingId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的會議紀錄"));
            }

            logger.LogInformation("Meeting updated successfully. MeetingId={MeetingId}, Title={Title}", id, meetingDto.Title);
            return Ok(ApiResult.SuccessResult("更新會議紀錄成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update meeting. MeetingId={MeetingId}", id);
            return this.ApiServerError("更新會議紀錄失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received meeting delete request. MeetingId={MeetingId}", id);

            var deleted = await meetingRepository.DeleteAsync(id);
            if (deleted == null)
            {
                logger.LogWarning("Meeting delete request could not find record. MeetingId={MeetingId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的會議紀錄"));
            }

            // 資料列由 repository 刪除，實體檔案要另外清掉（沒有 Cascade 可以依賴）。
            meetingFileStore.TryDeleteMedia(deleted.MediaRelativePath);
            meetingFileStore.TryDeleteTranscript(deleted.TranscriptRelativePath);

            logger.LogInformation("Meeting deleted successfully. MeetingId={MeetingId}", id);
            return Ok(ApiResult.SuccessResult("刪除會議紀錄成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete meeting. MeetingId={MeetingId}", id);
            return this.ApiServerError("刪除會議紀錄失敗", ex);
        }
    }
}
