using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Repositories;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class PromptTemplateController : ControllerBase
{
    private readonly ILogger<PromptTemplateController> logger;
    private readonly PromptTemplateRepository promptTemplateRepository;
    private readonly IMapper mapper;

    public PromptTemplateController(
        ILogger<PromptTemplateController> logger,
        PromptTemplateRepository promptTemplateRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.promptTemplateRepository = promptTemplateRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PromptTemplateDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received prompt template get request. PromptTemplateId={PromptTemplateId}", id);

            var promptTemplate = await promptTemplateRepository.GetByIdAsync(id);
            if (promptTemplate == null)
            {
                logger.LogWarning("Prompt template get request could not find record. PromptTemplateId={PromptTemplateId}", id);
                return NotFound(ApiResult<PromptTemplateDto>.NotFoundResult($"找不到 ID 為 {id} 的提示詞"));
            }

            var promptTemplateDto = mapper.Map<PromptTemplateDto>(promptTemplate);
            logger.LogInformation("Prompt template retrieved successfully. PromptTemplateId={PromptTemplateId}", id);
            return Ok(ApiResult<PromptTemplateDto>.SuccessResult(promptTemplateDto, "取得提示詞成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get prompt template. PromptTemplateId={PromptTemplateId}", id);
            return this.ApiServerError<PromptTemplateDto>("取得提示詞失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<PromptTemplateDto>>>> Search([FromBody] PromptTemplateSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received prompt template search request. Keyword={Keyword}, IsEnabled={IsEnabled}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}",
                request.Keyword,
                request.IsEnabled,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending);

            var pagedResult = await promptTemplateRepository.GetPagedAsync(request);
            var promptTemplateDtos = mapper.Map<List<PromptTemplateDto>>(pagedResult.Items);

            var result = new PagedResult<PromptTemplateDto>
            {
                Items = promptTemplateDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Prompt template search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}",
                result.Items.Count,
                result.TotalCount);

            return Ok(ApiResult<PagedResult<PromptTemplateDto>>.SuccessResult(result, "搜尋提示詞成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search prompt templates");
            return this.ApiServerError<PagedResult<PromptTemplateDto>>("搜尋提示詞失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<PromptTemplateDto>>> Create([FromBody] PromptTemplateCreateUpdateDto promptTemplateDto)
    {
        try
        {
            logger.LogDebug("Received prompt template create request. Name={Name}", promptTemplateDto.Name);

            if (await promptTemplateRepository.ExistsByNameAsync(promptTemplateDto.Name))
            {
                logger.LogWarning("Prompt template create request rejected because name already exists. Name={Name}", promptTemplateDto.Name);
                return Conflict(ApiResult<PromptTemplateDto>.ConflictResult($"提示詞名稱 '{promptTemplateDto.Name}' 已存在"));
            }

            var promptTemplate = mapper.Map<PromptTemplate>(promptTemplateDto);
            var created = await promptTemplateRepository.AddAsync(promptTemplate);
            var createdDto = mapper.Map<PromptTemplateDto>(created);

            logger.LogInformation("Prompt template created successfully. PromptTemplateId={PromptTemplateId}, Name={Name}", createdDto.Id, createdDto.Name);
            return Ok(ApiResult<PromptTemplateDto>.SuccessResult(createdDto, "新增提示詞成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create prompt template. Name={Name}", promptTemplateDto.Name);
            return this.ApiServerError<PromptTemplateDto>("新增提示詞失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] PromptTemplateCreateUpdateDto promptTemplateDto)
    {
        try
        {
            logger.LogDebug("Received prompt template update request. RouteId={RouteId}, PayloadId={PayloadId}, Name={Name}", id, promptTemplateDto.Id, promptTemplateDto.Name);

            if (id != promptTemplateDto.Id)
            {
                logger.LogWarning("Prompt template update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, promptTemplateDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (await promptTemplateRepository.ExistsByNameAsync(promptTemplateDto.Name, id))
            {
                logger.LogWarning("Prompt template update request rejected because name is already in use. PromptTemplateId={PromptTemplateId}, Name={Name}", id, promptTemplateDto.Name);
                return Conflict(ApiResult.ConflictResult($"提示詞名稱 '{promptTemplateDto.Name}' 已被其他提示詞使用"));
            }

            var promptTemplate = mapper.Map<PromptTemplate>(promptTemplateDto);
            var success = await promptTemplateRepository.UpdateAsync(promptTemplate);
            if (!success)
            {
                logger.LogWarning("Prompt template update request could not find record. PromptTemplateId={PromptTemplateId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的提示詞"));
            }

            logger.LogInformation("Prompt template updated successfully. PromptTemplateId={PromptTemplateId}, Name={Name}", id, promptTemplateDto.Name);
            return Ok(ApiResult.SuccessResult("更新提示詞成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update prompt template. PromptTemplateId={PromptTemplateId}", id);
            return this.ApiServerError("更新提示詞失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received prompt template delete request. PromptTemplateId={PromptTemplateId}", id);

            var success = await promptTemplateRepository.DeleteAsync(id);
            if (!success)
            {
                logger.LogWarning("Prompt template delete request could not find record. PromptTemplateId={PromptTemplateId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的提示詞"));
            }

            logger.LogInformation("Prompt template deleted successfully. PromptTemplateId={PromptTemplateId}", id);
            return Ok(ApiResult.SuccessResult("刪除提示詞成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete prompt template. PromptTemplateId={PromptTemplateId}", id);
            return this.ApiServerError("刪除提示詞失敗", ex);
        }
    }
}
