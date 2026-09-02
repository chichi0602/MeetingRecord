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
public class ProjectController : ControllerBase
{
    private readonly ILogger<ProjectController> logger;
    private readonly ProjectRepository projectRepository;
    private readonly IMapper mapper;

    public ProjectController(
        ILogger<ProjectController> logger,
        ProjectRepository projectRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.projectRepository = projectRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> GetById(int id, [FromQuery] bool includeRelatedData = false)
    {
        try
        {
            logger.LogDebug(
                "Received project get request. ProjectId={ProjectId}, IncludeRelatedData={IncludeRelatedData}",
                id,
                includeRelatedData);

            var project = await projectRepository.GetByIdAsync(id, includeRelatedData);

            if (project == null)
            {
                logger.LogWarning("Project get request could not find record. ProjectId={ProjectId}", id);
                return NotFound(ApiResult<ProjectDto>.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            var projectDto = mapper.Map<ProjectDto>(project);
            logger.LogInformation("Project retrieved successfully. ProjectId={ProjectId}", id);

            return Ok(ApiResult<ProjectDto>.SuccessResult(projectDto, "取得專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get project. ProjectId={ProjectId}", id);
            return this.ApiServerError<ProjectDto>("取得專案失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<ProjectDto>>>> Search([FromBody] ProjectSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received project search request. Keyword={Keyword}, Owner={Owner}, Status={Status}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, IncludeRelatedData={IncludeRelatedData}",
                request.Keyword,
                request.Owner,
                request.Status,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending,
                request.IncludeRelatedData);

            var pagedResult = await projectRepository.GetPagedAsync(request);
            var projectDtos = mapper.Map<List<ProjectDto>>(pagedResult.Items);

            var result = new PagedResult<ProjectDto>
            {
                Items = projectDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Project search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}, PageIndex={PageIndex}, PageSize={PageSize}",
                result.Items.Count,
                result.TotalCount,
                result.PageIndex,
                result.PageSize);

            return Ok(ApiResult<PagedResult<ProjectDto>>.SuccessResult(result, "搜尋專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search projects");
            return this.ApiServerError<PagedResult<ProjectDto>>("搜尋專案失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> Create([FromBody] ProjectCreateUpdateDto projectDto)
    {
        try
        {
            logger.LogDebug(
                "Received project create request. Title={Title}, Owner={Owner}, Status={Status}",
                projectDto.Title,
                projectDto.Owner,
                projectDto.Status);

            if (await projectRepository.ExistsByNameAsync(projectDto.Title))
            {
                logger.LogWarning(
                    "Project create request rejected because title already exists. Title={Title}",
                    projectDto.Title);
                return Conflict(ApiResult<ProjectDto>.ConflictResult($"專案名稱 '{projectDto.Title}' 已存在"));
            }

            var project = mapper.Map<Project>(projectDto);
            var createdProject = await projectRepository.AddAsync(project);
            var createdProjectDto = mapper.Map<ProjectDto>(createdProject);

            logger.LogInformation(
                "Project created successfully. ProjectId={ProjectId}, Title={Title}",
                createdProjectDto.Id,
                createdProjectDto.Title);

            return Ok(ApiResult<ProjectDto>.SuccessResult(createdProjectDto, "新增專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create project. Title={Title}", projectDto.Title);
            return this.ApiServerError<ProjectDto>("新增專案失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] ProjectCreateUpdateDto projectDto)
    {
        try
        {
            logger.LogDebug(
                "Received project update request. RouteProjectId={RouteProjectId}, PayloadProjectId={PayloadProjectId}, Title={Title}",
                id,
                projectDto.Id,
                projectDto.Title);

            if (id != projectDto.Id)
            {
                logger.LogWarning(
                    "Project update request rejected because route id and payload id do not match. RouteProjectId={RouteProjectId}, PayloadProjectId={PayloadProjectId}",
                    id,
                    projectDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (await projectRepository.ExistsByNameAsync(projectDto.Title, id))
            {
                logger.LogWarning(
                    "Project update request rejected because title is already in use. ProjectId={ProjectId}, Title={Title}",
                    id,
                    projectDto.Title);
                return Conflict(ApiResult.ConflictResult($"專案名稱 '{projectDto.Title}' 已被其他專案使用"));
            }

            var project = mapper.Map<Project>(projectDto);
            var success = await projectRepository.UpdateAsync(project);

            if (!success)
            {
                logger.LogWarning("Project update request could not find record. ProjectId={ProjectId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", id, projectDto.Title);
            return Ok(ApiResult.SuccessResult("更新專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update project. ProjectId={ProjectId}", id);
            return this.ApiServerError("更新專案失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received project delete request. ProjectId={ProjectId}", id);

            var success = await projectRepository.DeleteAsync(id);

            if (!success)
            {
                logger.LogWarning("Project delete request could not find record. ProjectId={ProjectId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}", id);
            return Ok(ApiResult.SuccessResult("刪除專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete project. ProjectId={ProjectId}", id);

            if (ex.InnerException?.Message.Contains("DELETE statement conflicted") == true)
            {
                logger.LogWarning(
                    "Project delete request rejected because related data still exists. ProjectId={ProjectId}",
                    id);
                return BadRequest(ApiResult.FailureResult("此專案仍有關聯資料，無法刪除"));
            }

            return this.ApiServerError("刪除專案失敗", ex);
        }
    }
}
