using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 分類 API。1.1.7 起呼叫畫面用的同一個 <see cref="CategoryService"/>，規則與畫面一致：
/// 名稱重複判斷去頭尾空白、不分大小寫；新增與修改一併同步負責團隊；另有啟用切換與可選團隊。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public class CategoryController : ControllerBase
{
    private readonly ILogger<CategoryController> logger;
    private readonly CategoryService categoryService;

    public CategoryController(ILogger<CategoryController> logger, CategoryService categoryService)
    {
        this.logger = logger;
        this.categoryService = categoryService;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<CategoryDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received category get request. CategoryId={CategoryId}", id);

            var category = await categoryService.GetAsync(id);
            if (category.Id == 0)
            {
                logger.LogWarning("Category get request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult<CategoryDto>.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            return Ok(ApiResult<CategoryDto>.SuccessResult(ToDto(category), "取得分類成功"));
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

            var sortField = ResolveSortField(request.SortBy);
            var data = await categoryService.GetAsync(
                new DataRequest
                {
                    Search = request.Keyword ?? string.Empty,
                    SortField = sortField,
                    SortDescending = sortField.Length == 0 ? null : request.SortDescending,
                    CurrentPage = request.PageIndex,
                    PageSize = request.PageSize,
                },
                request.IsEnabled);

            var result = new PagedResult<CategoryDto>
            {
                Items = data.Result.Select(ToDto).ToList(),
                TotalCount = data.Count,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(data.Count / (double)request.PageSize),
            };

            logger.LogInformation("Category search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}", result.Items.Count, result.TotalCount);
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

            var model = ToModel(categoryDto);
            model.Id = 0;
            var check = await categoryService.BeforeAddCheckAsync(model);
            if (!check.Success)
            {
                logger.LogWarning("Category create request rejected by validation. Name={Name}, Message={Message}", categoryDto.Name, check.Message);
                return Conflict(ApiResult<CategoryDto>.ConflictResult(check.Message));
            }

            var added = await categoryService.AddAsync(model, AuditVia.Api);
            if (!added.Success)
            {
                return BadRequest(ApiResult<CategoryDto>.FailureResult(added.Message));
            }

            if (categoryDto.TeamIds is { } teamIds)
            {
                await categoryService.SyncTeamsAsync(model.Id, teamIds);
            }

            var created = await categoryService.GetAsync(model.Id);
            logger.LogInformation("Category created successfully. CategoryId={CategoryId}, Name={Name}", created.Id, created.Name);
            return Ok(ApiResult<CategoryDto>.SuccessResult(ToDto(created), "新增分類成功"));
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

            if ((await categoryService.GetAsync(id)).Id == 0)
            {
                logger.LogWarning("Category update request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            var model = ToModel(categoryDto);
            var check = await categoryService.BeforeUpdateCheckAsync(model);
            if (!check.Success)
            {
                logger.LogWarning("Category update request rejected by validation. CategoryId={CategoryId}, Message={Message}", id, check.Message);
                return Conflict(ApiResult.ConflictResult(check.Message));
            }

            var updated = await categoryService.UpdateAsync(model, AuditVia.Api);
            if (!updated.Success)
            {
                return BadRequest(ApiResult.FailureResult(updated.Message));
            }

            // 沒帶 teamIds 的呼叫端（1.1.7 以前的程式）不動團隊；帶空陣列才是清空。
            if (categoryDto.TeamIds is { } teamIds)
            {
                await categoryService.SyncTeamsAsync(id, teamIds);
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

            if ((await categoryService.GetAsync(id)).Id == 0)
            {
                logger.LogWarning("Category delete request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            var deleted = await categoryService.DeleteAsync(id, AuditVia.Api);
            if (!deleted.Success)
            {
                return BadRequest(ApiResult.FailureResult(deleted.Message));
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

    /// <summary>清單上直接切換啟用狀態（1.1.7）。</summary>
    [HttpPut("{id}/enabled")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> SetEnabled(int id, [FromBody] SetEnabledRequestDto request)
    {
        try
        {
            if ((await categoryService.GetAsync(id)).Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            var result = await categoryService.SetEnabledAsync(id, request.IsEnabled);
            if (!result.Success)
            {
                return BadRequest(ApiResult.FailureResult(result.Message));
            }

            return Ok(ApiResult.SuccessResult(request.IsEnabled ? "已啟用分類" : "已停用分類"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set category state. CategoryId={CategoryId}", id);
            return this.ApiServerError("更新分類狀態失敗", ex);
        }
    }

    /// <summary>「負責團隊（參考）」可選的團隊：啟用中的團隊（1.1.7）。</summary>
    [HttpGet("teams/selectable")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TeamOptionDto>>>> GetSelectableTeams()
    {
        try
        {
            var teams = await categoryService.GetSelectableTeamsAsync();
            return Ok(ApiResult<List<TeamOptionDto>>.SuccessResult(teams.Select(x => new TeamOptionDto { Id = x.Id, Name = x.Name }).ToList(), "取得可選團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load selectable teams for categories");
            return this.ApiServerError<List<TeamOptionDto>>("取得可選團隊失敗", ex);
        }
    }

    /// <summary>API 的排序欄位不分大小寫（name、isenabled、updatedat、createdat），對到服務認得的欄位名；其他值用預設排序。</summary>
    private static string ResolveSortField(string? sortBy) => sortBy?.ToLowerInvariant() switch
    {
        "name" => nameof(CategoryAdapterModel.Name),
        "isenabled" => nameof(CategoryAdapterModel.IsEnabled),
        "updatedat" => nameof(CategoryAdapterModel.UpdatedAt),
        "createdat" => nameof(CategoryAdapterModel.CreatedAt),
        _ => string.Empty,
    };

    private static CategoryAdapterModel ToModel(CategoryCreateUpdateDto dto) => new()
    {
        Id = dto.Id ?? 0,
        Name = dto.Name,
        Description = dto.Description,
        IsEnabled = dto.IsEnabled,
        TeamIds = dto.TeamIds ?? [],
    };

    private static CategoryDto ToDto(CategoryAdapterModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Description = model.Description,
        IsEnabled = model.IsEnabled,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        TeamIds = model.TeamIds,
        TeamNames = model.TeamNames,
    };
}
