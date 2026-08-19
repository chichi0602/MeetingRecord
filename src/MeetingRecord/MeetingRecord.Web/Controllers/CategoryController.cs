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
public class CategoryController : ControllerBase
{
    private readonly ILogger<CategoryController> logger;
    private readonly CategoryRepository categoryRepository;
    private readonly IMapper mapper;

    public CategoryController(
        ILogger<CategoryController> logger,
        CategoryRepository categoryRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.categoryRepository = categoryRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<CategoryDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received category get request. CategoryId={CategoryId}", id);

            var category = await categoryRepository.GetByIdAsync(id);
            if (category == null)
            {
                logger.LogWarning("Category get request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult<CategoryDto>.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            var categoryDto = mapper.Map<CategoryDto>(category);
            logger.LogInformation("Category retrieved successfully. CategoryId={CategoryId}", id);
            return Ok(ApiResult<CategoryDto>.SuccessResult(categoryDto, "取得分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get category. CategoryId={CategoryId}", id);
            return this.ApiServerError<CategoryDto>("取得分類失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<CategoryDto>>>> Search([FromBody] CategorySearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received category search request. Keyword={Keyword}, IsEnabled={IsEnabled}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}",
                request.Keyword,
                request.IsEnabled,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending);

            var pagedResult = await categoryRepository.GetPagedAsync(request);
            var categoryDtos = mapper.Map<List<CategoryDto>>(pagedResult.Items);

            var result = new PagedResult<CategoryDto>
            {
                Items = categoryDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Category search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}",
                result.Items.Count,
                result.TotalCount);

            return Ok(ApiResult<PagedResult<CategoryDto>>.SuccessResult(result, "搜尋分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search categories");
            return this.ApiServerError<PagedResult<CategoryDto>>("搜尋分類失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<CategoryDto>>> Create([FromBody] CategoryCreateUpdateDto categoryDto)
    {
        try
        {
            logger.LogDebug("Received category create request. Name={Name}", categoryDto.Name);

            if (await categoryRepository.ExistsByNameAsync(categoryDto.Name))
            {
                logger.LogWarning("Category create request rejected because name already exists. Name={Name}", categoryDto.Name);
                return Conflict(ApiResult<CategoryDto>.ConflictResult($"分類名稱 '{categoryDto.Name}' 已存在"));
            }

            var category = mapper.Map<Category>(categoryDto);
            var created = await categoryRepository.AddAsync(category);
            var createdDto = mapper.Map<CategoryDto>(created);

            logger.LogInformation("Category created successfully. CategoryId={CategoryId}, Name={Name}", createdDto.Id, createdDto.Name);
            return Ok(ApiResult<CategoryDto>.SuccessResult(createdDto, "新增分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create category. Name={Name}", categoryDto.Name);
            return this.ApiServerError<CategoryDto>("新增分類失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] CategoryCreateUpdateDto categoryDto)
    {
        try
        {
            logger.LogDebug("Received category update request. RouteId={RouteId}, PayloadId={PayloadId}, Name={Name}", id, categoryDto.Id, categoryDto.Name);

            if (id != categoryDto.Id)
            {
                logger.LogWarning("Category update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, categoryDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (await categoryRepository.ExistsByNameAsync(categoryDto.Name, id))
            {
                logger.LogWarning("Category update request rejected because name is already in use. CategoryId={CategoryId}, Name={Name}", id, categoryDto.Name);
                return Conflict(ApiResult.ConflictResult($"分類名稱 '{categoryDto.Name}' 已被其他分類使用"));
            }

            var category = mapper.Map<Category>(categoryDto);
            var success = await categoryRepository.UpdateAsync(category);
            if (!success)
            {
                logger.LogWarning("Category update request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            logger.LogInformation("Category updated successfully. CategoryId={CategoryId}, Name={Name}", id, categoryDto.Name);
            return Ok(ApiResult.SuccessResult("更新分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update category. CategoryId={CategoryId}", id);
            return this.ApiServerError("更新分類失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received category delete request. CategoryId={CategoryId}", id);

            var success = await categoryRepository.DeleteAsync(id);
            if (!success)
            {
                logger.LogWarning("Category delete request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            logger.LogInformation("Category deleted successfully. CategoryId={CategoryId}", id);
            return Ok(ApiResult.SuccessResult("刪除分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete category. CategoryId={CategoryId}", id);
            return this.ApiServerError("刪除分類失敗", ex);
        }
    }
}
