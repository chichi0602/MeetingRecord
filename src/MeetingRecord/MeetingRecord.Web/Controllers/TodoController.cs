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
public class TodoController : ControllerBase
{
    private readonly ILogger<TodoController> logger;
    private readonly TodoRepository todoRepository;
    private readonly IMapper mapper;

    public TodoController(
        ILogger<TodoController> logger,
        TodoRepository todoRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.todoRepository = todoRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<TodoDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received todo get request. TodoId={TodoId}", id);

            var todo = await todoRepository.GetByIdAsync(id);
            if (todo == null)
            {
                logger.LogWarning("Todo get request could not find record. TodoId={TodoId}", id);
                return NotFound(ApiResult<TodoDto>.NotFoundResult($"找不到 ID 為 {id} 的待辦事項"));
            }

            var todoDto = mapper.Map<TodoDto>(todo);
            logger.LogInformation("Todo retrieved successfully. TodoId={TodoId}", id);
            return Ok(ApiResult<TodoDto>.SuccessResult(todoDto, "取得待辦事項成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get todo. TodoId={TodoId}", id);
            return this.ApiServerError<TodoDto>("取得待辦事項失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<TodoDto>>>> Search([FromBody] TodoSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received todo search request. Keyword={Keyword}, ProjectId={ProjectId}, Status={Status}, PageIndex={PageIndex}, PageSize={PageSize}",
                request.Keyword,
                request.ProjectId,
                request.Status,
                request.PageIndex,
                request.PageSize);

            var pagedResult = await todoRepository.GetPagedAsync(request);
            var todoDtos = mapper.Map<List<TodoDto>>(pagedResult.Items);

            var result = new PagedResult<TodoDto>
            {
                Items = todoDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Todo search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}",
                result.Items.Count,
                result.TotalCount);

            return Ok(ApiResult<PagedResult<TodoDto>>.SuccessResult(result, "搜尋待辦事項成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search todos");
            return this.ApiServerError<PagedResult<TodoDto>>("搜尋待辦事項失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<TodoDto>>> Create([FromBody] TodoCreateUpdateDto todoDto)
    {
        try
        {
            logger.LogDebug("Received todo create request. Title={Title}, ProjectId={ProjectId}", todoDto.Title, todoDto.ProjectId);

            if (!await todoRepository.ProjectExistsAsync(todoDto.ProjectId))
            {
                logger.LogWarning("Todo create request rejected because project was not found. ProjectId={ProjectId}", todoDto.ProjectId);
                return BadRequest(ApiResult<TodoDto>.ValidationError($"找不到 ID 為 {todoDto.ProjectId} 的專案項目"));
            }

            var todo = mapper.Map<Todo>(todoDto);
            var created = await todoRepository.AddAsync(todo);

            // 重新載入才帶得到 Project／Meeting 導覽屬性，
            // 否則回應的 ProjectTitle／MeetingTitle 會是空的。
            var reloaded = await todoRepository.GetByIdAsync(created.Id) ?? created;
            var createdDto = mapper.Map<TodoDto>(reloaded);

            logger.LogInformation("Todo created successfully. TodoId={TodoId}, Title={Title}", createdDto.Id, createdDto.Title);
            return Ok(ApiResult<TodoDto>.SuccessResult(createdDto, "新增待辦事項成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create todo. Title={Title}", todoDto.Title);
            return this.ApiServerError<TodoDto>("新增待辦事項失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] TodoCreateUpdateDto todoDto)
    {
        try
        {
            logger.LogDebug("Received todo update request. RouteId={RouteId}, PayloadId={PayloadId}", id, todoDto.Id);

            if (id != todoDto.Id)
            {
                logger.LogWarning("Todo update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, todoDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (!await todoRepository.ProjectExistsAsync(todoDto.ProjectId))
            {
                logger.LogWarning("Todo update request rejected because project was not found. ProjectId={ProjectId}", todoDto.ProjectId);
                return BadRequest(ApiResult.ValidationError($"找不到 ID 為 {todoDto.ProjectId} 的專案項目"));
            }

            var todo = mapper.Map<Todo>(todoDto);
            var success = await todoRepository.UpdateAsync(todo);
            if (!success)
            {
                logger.LogWarning("Todo update request could not find record. TodoId={TodoId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的待辦事項"));
            }

            logger.LogInformation("Todo updated successfully. TodoId={TodoId}, Title={Title}", id, todoDto.Title);
            return Ok(ApiResult.SuccessResult("更新待辦事項成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update todo. TodoId={TodoId}", id);
            return this.ApiServerError("更新待辦事項失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received todo delete request. TodoId={TodoId}", id);

            var success = await todoRepository.DeleteAsync(id);
            if (!success)
            {
                logger.LogWarning("Todo delete request could not find record. TodoId={TodoId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的待辦事項"));
            }

            logger.LogInformation("Todo deleted successfully. TodoId={TodoId}", id);
            return Ok(ApiResult.SuccessResult("刪除待辦事項成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete todo. TodoId={TodoId}", id);
            return this.ApiServerError("刪除待辦事項失敗", ex);
        }
    }
}
