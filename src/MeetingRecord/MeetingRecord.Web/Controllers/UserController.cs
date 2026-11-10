using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Users;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 使用者管理 API（1.1.10，WASM 遷移），**只限管理者**（<see cref="AdminOnlyAttribute"/>），規則都在 <see cref="MyUserService"/>：
/// 不能刪除或停用自己與 support、不能拿掉自己的管理者身分、帳號不可重複、替別人重設密碼時對方下次登入要改。
/// ⚠️ 回應一律用 <see cref="UserDto"/>：不含密碼雜湊、Salt、兩步驟驗證金鑰、Google 編號。
/// </summary>
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public class UserController : ControllerBase
{
    private readonly ILogger<UserController> logger;
    private readonly MyUserService myUserService;
    private readonly TeamService teamService;
    private readonly CategoryService categoryService;

    public UserController(ILogger<UserController> logger, MyUserService myUserService, TeamService teamService, CategoryService categoryService)
    {
        this.logger = logger;
        this.myUserService = myUserService;
        this.teamService = teamService;
        this.categoryService = categoryService;
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<UserDto>>>> Search([FromBody] UserSearchRequestDto request)
    {
        try
        {
            var sortField = ResolveSortField(request.SortBy);
            var data = await myUserService.GetAsync(new DataRequest
            {
                Search = request.Keyword ?? string.Empty,
                SortField = sortField,
                SortDescending = sortField.Length == 0 ? null : request.SortDescending,
                CurrentPage = request.PageIndex,
                PageSize = request.PageSize,
            });

            var result = new PagedResult<UserDto>
            {
                Items = data.Result.Select(ToDto).ToList(),
                TotalCount = data.Count,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(data.Count / (double)request.PageSize),
            };
            return Ok(ApiResult<PagedResult<UserDto>>.SuccessResult(result, "搜尋使用者成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search users");
            return this.ApiServerError<PagedResult<UserDto>>("搜尋使用者失敗", ex);
        }
    }

    /// <summary>單筆，含額外角色與團隊（編輯視窗回填用）。</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResult<UserDto>>> GetById(int id)
    {
        try
        {
            var user = await myUserService.GetAsync(id);
            if (user.Id == 0)
            {
                return NotFound(ApiResult<UserDto>.NotFoundResult($"找不到 ID 為 {id} 的使用者"));
            }

            return Ok(ApiResult<UserDto>.SuccessResult(await ToDetailDtoAsync(user), "取得使用者成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get user. UserId={UserId}", id);
            return this.ApiServerError<UserDto>("取得使用者失敗", ex);
        }
    }

    [HttpPost]
    public async Task<ActionResult<ApiResult<UserDto>>> Create([FromBody] UserCreateUpdateDto dto)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.Password))
            {
                return BadRequest(ApiResult<UserDto>.FailureResult("新增使用者時必須輸入密碼。"));
            }

            var model = new MyUserAdapterModel
            {
                Account = dto.Account,
                Password = dto.Password,
                Name = dto.Name,
                Email = dto.Email,
                Status = dto.Status,
                IsAdmin = dto.IsAdmin,
                IsExternal = dto.IsExternal,
                RoleViewId = dto.RoleViewId,
                AdditionalRoleIds = dto.AdditionalRoleIds ?? [],
                TeamNames = dto.TeamNames ?? [],
            };

            var check = await myUserService.BeforeAddCheckAsync(model);
            if (!check.Success)
            {
                return Conflict(ApiResult<UserDto>.ConflictResult(check.Message));
            }

            var added = await myUserService.AddAsync(model);
            if (!added.Success)
            {
                return BadRequest(ApiResult<UserDto>.FailureResult(added.Message));
            }

            logger.LogInformation("User created through API. UserId={UserId}, Account={Account}", model.Id, model.Account);
            return Ok(ApiResult<UserDto>.SuccessResult(await ToDetailDtoAsync(await myUserService.GetAsync(model.Id)), "新增使用者成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create user. Account={Account}", dto.Account);
            return this.ApiServerError<UserDto>("新增使用者失敗", ex);
        }
    }

    /// <summary>
    /// 修改：先讀出原資料，再套上可改的欄位——服務整筆蓋回資料庫，畫面以外的欄位（Google 登入綁定等）不能被清掉。
    /// 額外角色與團隊 null＝不動；密碼留白＝不變。
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] UserCreateUpdateDto dto)
    {
        try
        {
            if (id != dto.Id)
            {
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            var model = await myUserService.GetAsync(id);
            if (model.Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的使用者"));
            }

            var (additionalRoleIds, teamNames) = await myUserService.GetUserAssignmentsAsync(id);
            model.Account = dto.Account;
            model.Password = dto.Password ?? string.Empty;
            model.Name = dto.Name;
            model.Email = dto.Email;
            model.Status = dto.Status;
            model.IsAdmin = dto.IsAdmin;
            model.IsExternal = dto.IsExternal;
            model.RoleViewId = dto.RoleViewId;
            model.AdditionalRoleIds = dto.AdditionalRoleIds ?? additionalRoleIds;
            model.TeamNames = dto.TeamNames ?? teamNames;

            var check = await myUserService.BeforeUpdateCheckAsync(model);
            if (!check.Success)
            {
                return Conflict(ApiResult.ConflictResult(check.Message));
            }

            var updated = await myUserService.UpdateAsync(model);
            if (!updated.Success)
            {
                return BadRequest(ApiResult.FailureResult(updated.Message));
            }

            return Ok(ApiResult.SuccessResult("更新使用者成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update user. UserId={UserId}", id);
            return this.ApiServerError("更新使用者失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            if ((await myUserService.GetAsync(id)).Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的使用者"));
            }

            var deleted = await myUserService.DeleteAsync(id);
            return deleted.Success
                ? Ok(ApiResult.SuccessResult("刪除使用者成功"))
                : BadRequest(ApiResult.FailureResult(deleted.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete user. UserId={UserId}", id);
            return this.ApiServerError("刪除使用者失敗", ex);
        }
    }

    /// <summary>清單上直接啟用／停用帳號。</summary>
    [HttpPut("{id}/status")]
    public async Task<ActionResult<ApiResult>> SetStatus(int id, [FromBody] SetEnabledRequestDto request)
    {
        try
        {
            if ((await myUserService.GetAsync(id)).Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的使用者"));
            }

            var result = await myUserService.SetStatusAsync(id, request.IsEnabled);
            return result.Success
                ? Ok(ApiResult.SuccessResult(request.IsEnabled ? "已啟用帳號" : "已停用帳號"))
                : BadRequest(ApiResult.FailureResult(result.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set user status. UserId={UserId}", id);
            return this.ApiServerError("更新帳號狀態失敗", ex);
        }
    }

    /// <summary>批次把勾選的人加入或移出一個團隊，回傳實際異動的人數（已經在／本來就不在的略過）。</summary>
    [HttpPost("teams/batch")]
    public async Task<ActionResult<ApiResult<int>>> BatchTeam([FromBody] UserTeamBatchRequestDto request)
    {
        try
        {
            if ((await teamService.GetAsync(request.TeamId)).Id == 0)
            {
                return NotFound(ApiResult<int>.NotFoundResult($"找不到 ID 為 {request.TeamId} 的團隊"));
            }

            var changed = request.Add
                ? await myUserService.AddUsersToTeamAsync(request.UserIds, request.TeamId)
                : await myUserService.RemoveUsersFromTeamAsync(request.UserIds, request.TeamId);
            return Ok(ApiResult<int>.SuccessResult(changed, request.Add ? "已加入團隊" : "已移出團隊"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change team membership in batch. TeamId={TeamId}", request.TeamId);
            return this.ApiServerError<int>("批次調整團隊失敗", ex);
        }
    }

    /// <summary>角色下拉的選項；<c>isDefault</c> 標出新建帳號預設的角色（畫面新增時預選它）。</summary>
    [HttpGet("roles")]
    public async Task<ActionResult<ApiResult<List<RoleOptionDto>>>> GetRoles()
    {
        try
        {
            var roles = await myUserService.GetRoleViewsAsync();
            return Ok(ApiResult<List<RoleOptionDto>>.SuccessResult(
                roles.Select(x => new RoleOptionDto { Id = x.Id, Name = x.Name, IsDefault = x.Name == MagicObjectHelper.預設角色 }).ToList(),
                "取得角色成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load roles for user management");
            return this.ApiServerError<List<RoleOptionDto>>("取得角色失敗", ex);
        }
    }

    /// <summary>啟用中的團隊（表單多選用名稱、批次用編號）。</summary>
    [HttpGet("teams")]
    public async Task<ActionResult<ApiResult<List<TeamOptionDto>>>> GetTeams()
    {
        try
        {
            // 「啟用中的團隊、依名稱排序」與分類頁的團隊選項同一份查詢。
            var teams = await categoryService.GetSelectableTeamsAsync();
            return Ok(ApiResult<List<TeamOptionDto>>.SuccessResult(teams.Select(x => new TeamOptionDto { Id = x.Id, Name = x.Name }).ToList(), "取得團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load teams for user management");
            return this.ApiServerError<List<TeamOptionDto>>("取得團隊失敗", ex);
        }
    }

    /// <summary>API 的排序欄位不分大小寫，對到清單欄位（帳號、名稱、Email、角色、狀態、建立與更新時間）；其他值不排序。</summary>
    private static string ResolveSortField(string? sortBy) => sortBy?.ToLowerInvariant() switch
    {
        "account" => nameof(MyUserAdapterModel.Account),
        "name" => nameof(MyUserAdapterModel.Name),
        "email" => nameof(MyUserAdapterModel.Email),
        "roletext" => nameof(MyUserAdapterModel.RoleText),
        "statustext" => nameof(MyUserAdapterModel.StatusText),
        "createat" => nameof(MyUserAdapterModel.CreateAt),
        "updateat" => nameof(MyUserAdapterModel.UpdateAt),
        _ => string.Empty,
    };

    private async Task<UserDto> ToDetailDtoAsync(MyUserAdapterModel model)
    {
        var dto = ToDto(model);
        var (additionalRoleIds, teamNames) = await myUserService.GetUserAssignmentsAsync(model.Id);
        dto.AdditionalRoleIds = additionalRoleIds;
        dto.TeamNames = teamNames;
        return dto;
    }

    private static UserDto ToDto(MyUserAdapterModel model) => new()
    {
        Id = model.Id,
        Account = model.Account,
        Name = model.Name,
        Email = model.Email,
        Status = model.Status,
        IsAdmin = model.IsAdmin,
        IsExternal = model.IsExternal,
        RoleViewId = model.RoleViewId,
        TeamNames = model.TeamNames,
        RoleNames = model.RoleNames,
        CreateAt = model.CreateAt,
        UpdateAt = model.UpdateAt,
    };
}
