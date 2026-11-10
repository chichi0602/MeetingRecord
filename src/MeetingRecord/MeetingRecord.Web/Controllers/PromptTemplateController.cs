using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 會議紀錄提示詞 API。1.1.9（WASM 遷移）起呼叫畫面用的同一個 <see cref="PromptTemplateService"/>，規則與畫面一致：
/// 名稱重複判斷去頭尾空白、不分大小寫；分類一律存成標準格式。另有啟用切換、內建範本、啟用中的分類名稱。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public class PromptTemplateController : ControllerBase
{
    private readonly ILogger<PromptTemplateController> logger;
    private readonly PromptTemplateService promptTemplateService;
    private readonly CategoryService categoryService;

    public PromptTemplateController(
        ILogger<PromptTemplateController> logger,
        PromptTemplateService promptTemplateService,
        CategoryService categoryService)
    {
        this.logger = logger;
        this.promptTemplateService = promptTemplateService;
        this.categoryService = categoryService;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PromptTemplateDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received prompt template get request. PromptTemplateId={PromptTemplateId}", id);

            var template = await promptTemplateService.GetAsync(id);
            if (template.Id == 0)
            {
                logger.LogWarning("Prompt template get request could not find record. PromptTemplateId={PromptTemplateId}", id);
                return NotFound(ApiResult<PromptTemplateDto>.NotFoundResult($"找不到 ID 為 {id} 的提示詞"));
            }

            return Ok(ApiResult<PromptTemplateDto>.SuccessResult(ToDto(template), "取得提示詞成功"));
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

            var sortField = ResolveSortField(request.SortBy);
            var data = await promptTemplateService.GetAsync(
                new DataRequest
                {
                    Search = request.Keyword ?? string.Empty,
                    SortField = sortField,
                    SortDescending = sortField.Length == 0 ? null : request.SortDescending,
                    CurrentPage = request.PageIndex,
                    PageSize = request.PageSize,
                },
                request.IsEnabled);

            var result = new PagedResult<PromptTemplateDto>
            {
                Items = data.Result.Select(ToDto).ToList(),
                TotalCount = data.Count,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(data.Count / (double)request.PageSize),
            };

            logger.LogInformation("Prompt template search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}", result.Items.Count, result.TotalCount);
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

            var model = ToModel(promptTemplateDto);
            model.Id = 0;
            return await AddAsync(model);
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

            if ((await promptTemplateService.GetAsync(id)).Id == 0)
            {
                logger.LogWarning("Prompt template update request could not find record. PromptTemplateId={PromptTemplateId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的提示詞"));
            }

            var model = ToModel(promptTemplateDto);
            var check = await promptTemplateService.BeforeUpdateCheckAsync(model);
            if (!check.Success)
            {
                logger.LogWarning("Prompt template update request rejected by validation. PromptTemplateId={PromptTemplateId}, Message={Message}", id, check.Message);
                return Conflict(ApiResult.ConflictResult(check.Message));
            }

            var updated = await promptTemplateService.UpdateAsync(model, AuditVia.Api);
            if (!updated.Success)
            {
                return BadRequest(ApiResult.FailureResult(updated.Message));
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

            if ((await promptTemplateService.GetAsync(id)).Id == 0)
            {
                logger.LogWarning("Prompt template delete request could not find record. PromptTemplateId={PromptTemplateId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的提示詞"));
            }

            var deleted = await promptTemplateService.DeleteAsync(id, AuditVia.Api);
            if (!deleted.Success)
            {
                return BadRequest(ApiResult.FailureResult(deleted.Message));
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

    /// <summary>清單上直接切換啟用狀態（1.1.9）。</summary>
    [HttpPut("{id}/enabled")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> SetEnabled(int id, [FromBody] SetEnabledRequestDto request)
    {
        try
        {
            if ((await promptTemplateService.GetAsync(id)).Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的提示詞"));
            }

            var result = await promptTemplateService.SetEnabledAsync(id, request.IsEnabled);
            if (!result.Success)
            {
                return BadRequest(ApiResult.FailureResult(result.Message));
            }

            return Ok(ApiResult.SuccessResult(request.IsEnabled ? "已啟用提示詞" : "已停用提示詞"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set prompt template state. PromptTemplateId={PromptTemplateId}", id);
            return this.ApiServerError("更新提示詞狀態失敗", ex);
        }
    }

    /// <summary>內建範本的名稱（1.1.9，給「選擇內建範本」下拉）。要能新增提示詞才用得到。</summary>
    [HttpGet("presets")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.Create)]
    public ActionResult<ApiResult<List<string>>> GetPresets()
        => Ok(ApiResult<List<string>>.SuccessResult(PromptTemplatePresets.All.Select(x => x.Name).ToList(), "取得內建範本成功"));

    /// <summary>一次建立全部內建範本（1.1.9）。已有同名的略過，可以重複呼叫；訊息說明新增與略過幾筆。</summary>
    [HttpPost("presets")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult>> AddPresets(CancellationToken cancellationToken)
    {
        try
        {
            var result = await promptTemplateService.AddPresetsAsync(cancellationToken);
            return result.Success
                ? Ok(ApiResult.SuccessResult(result.Message))
                : BadRequest(ApiResult.FailureResult(result.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add prompt template presets");
            return this.ApiServerError("建立內建範本失敗", ex);
        }
    }

    /// <summary>只新增一個內建範本（1.1.9）。名稱不在內建清單回 404、已有同名回 409。</summary>
    [HttpPost("presets/single")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<PromptTemplateDto>>> AddPreset([FromBody] PresetNameRequestDto request)
    {
        try
        {
            var preset = PromptTemplatePresets.All.FirstOrDefault(x => x.Name == request.Name);
            if (preset is null)
            {
                return NotFound(ApiResult<PromptTemplateDto>.NotFoundResult($"找不到名為「{request.Name}」的內建範本。"));
            }

            var model = new PromptTemplateAdapterModel
            {
                Name = preset.Name,
                Content = preset.Content,
                Description = preset.Description,
                IsEnabled = true,
            };

            if (!(await promptTemplateService.BeforeAddCheckAsync(model)).Success)
            {
                return Conflict(ApiResult<PromptTemplateDto>.ConflictResult($"「{preset.Name}」已存在，未新增。"));
            }

            return await AddAsync(model, checkedAlready: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add prompt template preset. Name={Name}", request.Name);
            return this.ApiServerError<PromptTemplateDto>("新增內建範本失敗", ex);
        }
    }

    /// <summary>表單「分類」下拉的選項：啟用中的分類名稱（1.1.9）。</summary>
    [HttpGet("categories/enabled")]
    [HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<string>>>> GetEnabledCategories()
    {
        try
        {
            return Ok(ApiResult<List<string>>.SuccessResult(await categoryService.GetAllEnabledNamesAsync(), "取得分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load enabled categories for prompt templates");
            return this.ApiServerError<List<string>>("取得分類失敗", ex);
        }
    }

    /// <summary>新增的共同路徑：名稱重複回 409，成功回新建的那筆。</summary>
    private async Task<ActionResult<ApiResult<PromptTemplateDto>>> AddAsync(PromptTemplateAdapterModel model, bool checkedAlready = false)
    {
        if (!checkedAlready)
        {
            var check = await promptTemplateService.BeforeAddCheckAsync(model);
            if (!check.Success)
            {
                logger.LogWarning("Prompt template create request rejected by validation. Name={Name}, Message={Message}", model.Name, check.Message);
                return Conflict(ApiResult<PromptTemplateDto>.ConflictResult(check.Message));
            }
        }

        var added = await promptTemplateService.AddAsync(model, AuditVia.Api);
        if (!added.Success)
        {
            return BadRequest(ApiResult<PromptTemplateDto>.FailureResult(added.Message));
        }

        var created = await promptTemplateService.GetAsync(model.Id);
        logger.LogInformation("Prompt template created successfully. PromptTemplateId={PromptTemplateId}, Name={Name}", created.Id, created.Name);
        return Ok(ApiResult<PromptTemplateDto>.SuccessResult(ToDto(created), "新增提示詞成功"));
    }

    /// <summary>API 的排序欄位不分大小寫（name、isenabled、createdat、updatedat；內容刻意不排序），其他值用預設排序。</summary>
    private static string ResolveSortField(string? sortBy) => sortBy?.ToLowerInvariant() switch
    {
        "name" => nameof(PromptTemplateAdapterModel.Name),
        "isenabled" => nameof(PromptTemplateAdapterModel.IsEnabled),
        "createdat" => nameof(PromptTemplateAdapterModel.CreatedAt),
        "updatedat" => nameof(PromptTemplateAdapterModel.UpdatedAt),
        _ => string.Empty,
    };

    /// <summary>分類：<c>categoryNames</c> 有帶就用它；沒帶才把舊欄位 <c>categories</c>（儲存格式）拆開。存檔時服務會再正規化。</summary>
    private static PromptTemplateAdapterModel ToModel(PromptTemplateCreateUpdateDto dto) => new()
    {
        Id = dto.Id ?? 0,
        Name = dto.Name,
        Content = dto.Content,
        Description = dto.Description,
        IsEnabled = dto.IsEnabled,
        Categories = dto.CategoryNames ?? TagStringHelper.ToList(dto.Categories),
    };

    private static PromptTemplateDto ToDto(PromptTemplateAdapterModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Content = model.Content,
        Description = model.Description,
        IsEnabled = model.IsEnabled,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        Categories = TagStringHelper.ToStored(model.Categories),
        CategoryNames = model.Categories,
    };
}
