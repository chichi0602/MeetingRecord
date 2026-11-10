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
/// 團隊 API。1.1.8（WASM 遷移）起呼叫畫面用的同一個 <see cref="TeamService"/>，規則與畫面一致：
/// 名稱與代號重複判斷去頭尾空白、不分大小寫；新增與修改一併同步成員；仍是專案主責團隊的不能刪（管理者例外）。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public class TeamController : ControllerBase
{
    private readonly ILogger<TeamController> logger;
    private readonly TeamService teamService;

    public TeamController(ILogger<TeamController> logger, TeamService teamService)
    {
        this.logger = logger;
        this.teamService = teamService;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<TeamDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received team get request. TeamId={TeamId}", id);

            var team = await teamService.GetAsync(id);
            if (team.Id == 0)
            {
                logger.LogWarning("Team get request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult<TeamDto>.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            var dto = ToDto(team);
            dto.MemberIds = await teamService.GetMemberIdsAsync(id);
            dto.MemberCount = dto.MemberIds.Count;
            return Ok(ApiResult<TeamDto>.SuccessResult(dto, "取得團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get team. TeamId={TeamId}", id);
            return this.ApiServerError<TeamDto>("取得團隊失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<TeamDto>>>> Search([FromBody] TeamSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received team search request. Keyword={Keyword}, IsEnabled={IsEnabled}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}",
                request.Keyword,
                request.IsEnabled,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending);

            var sortField = ResolveSortField(request.SortBy);
            var data = await teamService.GetAsync(
                new DataRequest
                {
                    Search = request.Keyword ?? string.Empty,
                    SortField = sortField,
                    SortDescending = sortField.Length == 0 ? null : request.SortDescending,
                    CurrentPage = request.PageIndex,
                    PageSize = request.PageSize,
                },
                request.IsEnabled);

            var result = new PagedResult<TeamDto>
            {
                Items = data.Result.Select(ToDto).ToList(),
                TotalCount = data.Count,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(data.Count / (double)request.PageSize),
            };

            logger.LogInformation("Team search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}", result.Items.Count, result.TotalCount);
            return Ok(ApiResult<PagedResult<TeamDto>>.SuccessResult(result, "搜尋團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search teams");
            return this.ApiServerError<PagedResult<TeamDto>>("搜尋團隊失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<TeamDto>>> Create([FromBody] TeamCreateUpdateDto teamDto)
    {
        try
        {
            logger.LogDebug("Received team create request. Name={Name}, Code={Code}", teamDto.Name, teamDto.Code);

            var model = ToModel(teamDto);
            model.Id = 0;
            var check = await teamService.BeforeAddCheckAsync(model);
            if (!check.Success)
            {
                logger.LogWarning("Team create request rejected by validation. Name={Name}, Message={Message}", teamDto.Name, check.Message);
                return Conflict(ApiResult<TeamDto>.ConflictResult(check.Message));
            }

            var added = await teamService.AddAsync(model, AuditVia.Api);
            if (!added.Success)
            {
                return BadRequest(ApiResult<TeamDto>.FailureResult(added.Message));
            }

            if (teamDto.MemberIds is { } memberIds)
            {
                await teamService.SyncMembersAsync(model.Id, memberIds);
            }

            var created = ToDto(await teamService.GetAsync(model.Id));
            created.MemberIds = await teamService.GetMemberIdsAsync(model.Id);
            created.MemberCount = created.MemberIds.Count;
            logger.LogInformation("Team created successfully. TeamId={TeamId}, Name={Name}", created.Id, created.Name);
            return Ok(ApiResult<TeamDto>.SuccessResult(created, "新增團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create team. Name={Name}", teamDto.Name);
            return this.ApiServerError<TeamDto>("新增團隊失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] TeamCreateUpdateDto teamDto)
    {
        try
        {
            logger.LogDebug("Received team update request. RouteId={RouteId}, PayloadId={PayloadId}, Name={Name}", id, teamDto.Id, teamDto.Name);

            if (id != teamDto.Id)
            {
                logger.LogWarning("Team update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, teamDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if ((await teamService.GetAsync(id)).Id == 0)
            {
                logger.LogWarning("Team update request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            var model = ToModel(teamDto);
            var check = await teamService.BeforeUpdateCheckAsync(model);
            if (!check.Success)
            {
                logger.LogWarning("Team update request rejected by validation. TeamId={TeamId}, Message={Message}", id, check.Message);
                return Conflict(ApiResult.ConflictResult(check.Message));
            }

            var updated = await teamService.UpdateAsync(model, AuditVia.Api);
            if (!updated.Success)
            {
                return BadRequest(ApiResult.FailureResult(updated.Message));
            }

            // 沒帶 memberIds 的呼叫端（1.1.8 以前的程式）不動成員；帶空陣列才是清空。
            if (teamDto.MemberIds is { } memberIds)
            {
                await teamService.SyncMembersAsync(id, memberIds);
            }

            logger.LogInformation("Team updated successfully. TeamId={TeamId}, Name={Name}", id, teamDto.Name);
            return Ok(ApiResult.SuccessResult("更新團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update team. TeamId={TeamId}", id);
            return this.ApiServerError("更新團隊失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received team delete request. TeamId={TeamId}", id);

            if (await CheckDeletableAsync(id) is { } blocked)
            {
                return blocked;
            }

            var deleted = await teamService.DeleteAsync(id, AuditVia.Api);
            if (!deleted.Success)
            {
                return BadRequest(ApiResult.FailureResult(deleted.Message));
            }

            logger.LogInformation("Team deleted successfully. TeamId={TeamId}", id);
            return Ok(ApiResult.SuccessResult("刪除團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete team. TeamId={TeamId}", id);
            return this.ApiServerError("刪除團隊失敗", ex);
        }
    }

    /// <summary>
    /// 刪除前檢查（1.1.8）：畫面在跳確認框**之前**先問，不要讓使用者按了確認才失敗。
    /// 仍是專案主責團隊（非管理者）回 409 與原因；可以刪回 200。
    /// </summary>
    [HttpGet("{id}/delete-check")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> DeleteCheck(int id)
    {
        try
        {
            return await CheckDeletableAsync(id) ?? Ok(ApiResult.SuccessResult("可以刪除"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check team deletion. TeamId={TeamId}", id);
            return this.ApiServerError("刪除前檢查失敗", ex);
        }
    }

    /// <summary>清單上直接切換啟用狀態（1.1.8）。</summary>
    [HttpPut("{id}/enabled")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> SetEnabled(int id, [FromBody] SetEnabledRequestDto request)
    {
        try
        {
            if ((await teamService.GetAsync(id)).Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            var result = await teamService.SetEnabledAsync(id, request.IsEnabled);
            if (!result.Success)
            {
                return BadRequest(ApiResult.FailureResult(result.Message));
            }

            return Ok(ApiResult.SuccessResult(request.IsEnabled ? "已啟用團隊" : "已停用團隊"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set team state. TeamId={TeamId}", id);
            return this.ApiServerError("更新團隊狀態失敗", ex);
        }
    }

    /// <summary>
    /// 成員多選的選項：啟用中的帳號（1.1.8）。清單是全公司帳號的姓名與帳號，
    /// 只給能新增或修改團隊的人（編輯視窗才用得到），只有檢視權限拿不到。
    /// </summary>
    [HttpGet("members/selectable")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<List<MemberOptionDto>>>> GetSelectableMembers()
    {
        try
        {
            var members = await teamService.GetSelectableMembersAsync();
            return Ok(ApiResult<List<MemberOptionDto>>.SuccessResult(members.Select(x => new MemberOptionDto { Id = x.Id, Label = x.Label }).ToList(), "取得可選成員成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load selectable team members");
            return this.ApiServerError<List<MemberOptionDto>>("取得可選成員失敗", ex);
        }
    }

    /// <summary>不存在回 404、不能刪回 409；可以刪回 null。</summary>
    private async Task<ActionResult?> CheckDeletableAsync(int id)
    {
        var team = await teamService.GetAsync(id);
        if (team.Id == 0)
        {
            logger.LogWarning("Team delete could not find record. TeamId={TeamId}", id);
            return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
        }

        var check = await teamService.BeforeDeleteCheckAsync(team);
        if (!check.Success)
        {
            logger.LogWarning("Team delete rejected because it is still a primary team. TeamId={TeamId}", id);
            return Conflict(ApiResult.ConflictResult(check.Message));
        }

        return null;
    }

    /// <summary>API 的排序欄位不分大小寫（name、code、isenabled、updatedat、createdat），對到服務認得的欄位名；其他值用預設排序。</summary>
    private static string ResolveSortField(string? sortBy) => sortBy?.ToLowerInvariant() switch
    {
        "name" => nameof(TeamAdapterModel.Name),
        "code" => nameof(TeamAdapterModel.Code),
        "isenabled" => nameof(TeamAdapterModel.IsEnabled),
        "updatedat" => nameof(TeamAdapterModel.UpdatedAt),
        "createdat" => nameof(TeamAdapterModel.CreatedAt),
        _ => string.Empty,
    };

    private static TeamAdapterModel ToModel(TeamCreateUpdateDto dto) => new()
    {
        Id = dto.Id ?? 0,
        Name = dto.Name,
        Code = dto.Code,
        Description = dto.Description,
        IsEnabled = dto.IsEnabled,
        MemberIds = dto.MemberIds ?? [],
    };

    private static TeamDto ToDto(TeamAdapterModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Code = model.Code,
        Description = model.Description,
        IsEnabled = model.IsEnabled,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        MemberCount = model.MemberCount,
    };
}
