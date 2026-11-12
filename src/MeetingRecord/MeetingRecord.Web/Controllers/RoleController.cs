using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Roles;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Admins;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 角色管理 API（1.1.11），**只限管理者**（<see cref="AdminOnlyAttribute"/>）。規則都在 <see cref="RoleViewService"/>；
/// 這裡另外檢查權限樹：群組、頁面、動作都要在權限目錄（<see cref="RolePermissionService.GetRoleListPermissionAllName"/>）裡，
/// 免得 API 送進任意名稱變成權限鍵（以前只有畫面在把關）。
/// </summary>
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public class RoleController : ControllerBase
{
    private readonly ILogger<RoleController> logger;
    private readonly RoleViewService roleViewService;
    private readonly RolePermissionService rolePermissionService;

    public RoleController(ILogger<RoleController> logger, RoleViewService roleViewService, RolePermissionService rolePermissionService)
    {
        this.logger = logger;
        this.roleViewService = roleViewService;
        this.rolePermissionService = rolePermissionService;
    }

    /// <summary>清單（不帶權限樹）。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<RoleDto>>>> Search([FromBody] RoleSearchRequestDto request)
    {
        try
        {
            var sortField = ResolveSortField(request.SortBy);
            var data = await roleViewService.GetAsync(new DataRequest
            {
                Search = request.Keyword ?? string.Empty,
                SortField = sortField,
                SortDescending = sortField.Length == 0 ? null : request.SortDescending,
                CurrentPage = request.PageIndex,
                PageSize = request.PageSize,
            });

            var result = new PagedResult<RoleDto>
            {
                Items = data.Result.Select(x => ToDto(x, includePermissions: false)).ToList(),
                TotalCount = data.Count,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(data.Count / (double)request.PageSize),
            };
            return Ok(ApiResult<PagedResult<RoleDto>>.SuccessResult(result, "搜尋角色成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search roles");
            return this.ApiServerError<PagedResult<RoleDto>>("搜尋角色失敗", ex);
        }
    }

    /// <summary>單筆，含權限樹（編輯視窗用）。</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResult<RoleDto>>> GetById(int id)
    {
        try
        {
            var role = await roleViewService.GetAsync(id);
            return role.Id == 0
                ? NotFound(ApiResult<RoleDto>.NotFoundResult($"找不到 ID 為 {id} 的角色"))
                : Ok(ApiResult<RoleDto>.SuccessResult(ToDto(role, includePermissions: true), "取得角色成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get role. RoleViewId={RoleViewId}", id);
            return this.ApiServerError<RoleDto>("取得角色失敗", ex);
        }
    }

    /// <summary>全部未勾選的權限樹（新增角色用）。</summary>
    [HttpGet("permission-template")]
    public ActionResult<ApiResult<RolePermissionDto>> GetPermissionTemplate()
        => Ok(ApiResult<RolePermissionDto>.SuccessResult(ToDto(rolePermissionService.InitializePermissionSetting()), "取得權限範本成功"));

    [HttpPost]
    public async Task<ActionResult<ApiResult<RoleDto>>> Create([FromBody] RoleDto dto)
    {
        try
        {
            if (FindUnknownPermission(dto.RolePermission) is { } unknown)
            {
                return BadRequest(ApiResult<RoleDto>.FailureResult($"權限項目「{unknown}」不在權限目錄裡。"));
            }

            var model = new RoleViewAdapterModel
            {
                Name = dto.Name,
                RolePermission = ToModel(dto.RolePermission) ?? rolePermissionService.InitializePermissionSetting(),
                CreateAt = DateTime.Now,
                UpdateAt = DateTime.Now,
            };

            var check = await roleViewService.BeforeAddCheckAsync(model);
            if (!check.Success)
            {
                return Conflict(ApiResult<RoleDto>.ConflictResult(check.Message));
            }

            var added = await roleViewService.AddAsync(model);
            if (!added.Success)
            {
                return BadRequest(ApiResult<RoleDto>.FailureResult(added.Message));
            }

            return Ok(ApiResult<RoleDto>.SuccessResult(ToDto(await roleViewService.GetAsync(model.Id), includePermissions: true), "新增角色成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create role. Name={Name}", dto.Name);
            return this.ApiServerError<RoleDto>("新增角色失敗", ex);
        }
    }

    /// <summary>修改名稱與權限。建立時間沿用原值（服務整筆蓋回資料庫）。沒帶權限樹＝權限不變。</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] RoleDto dto)
    {
        try
        {
            if (id != dto.Id)
            {
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            var existing = await roleViewService.GetAsync(id);
            if (existing.Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的角色"));
            }

            if (FindUnknownPermission(dto.RolePermission) is { } unknown)
            {
                return BadRequest(ApiResult.FailureResult($"權限項目「{unknown}」不在權限目錄裡。"));
            }

            var model = new RoleViewAdapterModel
            {
                Id = id,
                Name = dto.Name,
                RolePermission = ToModel(dto.RolePermission) ?? existing.RolePermission,
                CreateAt = existing.CreateAt,
                UpdateAt = DateTime.Now,
            };

            var check = await roleViewService.BeforeUpdateCheckAsync(model);
            if (!check.Success)
            {
                return Conflict(ApiResult.ConflictResult(check.Message));
            }

            var updated = await roleViewService.UpdateAsync(model);
            return updated.Success
                ? Ok(ApiResult.SuccessResult("更新角色成功"))
                : BadRequest(ApiResult.FailureResult(updated.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update role. RoleViewId={RoleViewId}", id);
            return this.ApiServerError("更新角色失敗", ex);
        }
    }

    /// <summary>刪除：以它為主要角色的人改用其他角色，系統至少保留一個角色（規則在服務）。</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            if ((await roleViewService.GetAsync(id)).Id == 0)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的角色"));
            }

            var deleted = await roleViewService.DeleteAsync(id);
            return deleted.Success
                ? Ok(ApiResult.SuccessResult("刪除角色成功"))
                : BadRequest(ApiResult.FailureResult(deleted.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete role. RoleViewId={RoleViewId}", id);
            return this.ApiServerError("刪除角色失敗", ex);
        }
    }

    /// <summary>有多少人掛著這個角色（主要或額外），刪除前的確認視窗用。</summary>
    [HttpGet("{id}/user-count")]
    public async Task<ActionResult<ApiResult<int>>> GetUserCount(int id)
    {
        try
        {
            return Ok(ApiResult<int>.SuccessResult(await roleViewService.CountUsersAsync(id), "取得使用人數成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to count role users. RoleViewId={RoleViewId}", id);
            return this.ApiServerError<int>("取得使用人數失敗", ex);
        }
    }

    /// <summary>一次建好預設角色；已有同名的略過，可以重複呼叫。訊息說明新增與略過幾個。</summary>
    [HttpPost("presets")]
    public async Task<ActionResult<ApiResult>> AddPresets()
    {
        try
        {
            var result = await roleViewService.AddPresetsAsync();
            return result.Success
                ? Ok(ApiResult.SuccessResult(result.Message))
                : BadRequest(ApiResult.FailureResult(result.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add role presets");
            return this.ApiServerError("建立預設角色失敗", ex);
        }
    }

    /// <summary>權限樹裡第一個不在目錄裡的名稱（群組、頁面、或「頁面:動作」）；全部合法回 null。</summary>
    private string? FindUnknownPermission(RolePermissionDto? tree)
    {
        if (tree is null)
        {
            return null;
        }

        var catalog = rolePermissionService.GetRoleListPermissionAllName()
            .Where(x => x.Count > 0)
            .ToDictionary(x => x[0], x => x.Skip(1).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var group in tree.Groups)
        {
            if (!catalog.TryGetValue(group.Name, out var pages))
            {
                return group.Name;
            }

            foreach (var node in group.Permissions)
            {
                if (!pages.Contains(node.Name))
                {
                    return node.Name;
                }

                if (node.Actions.Keys.FirstOrDefault(action => !RolePermissionService.SupportedActions.Contains(action)) is { } badAction)
                {
                    return PermissionKey.For(node.Name, badAction);
                }
            }
        }

        return null;
    }

    private static string ResolveSortField(string? sortBy) => sortBy?.ToLowerInvariant() switch
    {
        "name" => nameof(RoleViewAdapterModel.Name),
        "createat" => nameof(RoleViewAdapterModel.CreateAt),
        "updateat" => nameof(RoleViewAdapterModel.UpdateAt),
        _ => string.Empty,
    };

    private static RoleDto ToDto(RoleViewAdapterModel model, bool includePermissions) => new()
    {
        Id = model.Id,
        Name = model.Name,
        CreateAt = model.CreateAt,
        UpdateAt = model.UpdateAt,
        RolePermission = includePermissions ? ToDto(model.RolePermission) : null,
    };

    private static RolePermissionDto ToDto(RolePermission tree) => new()
    {
        Groups = tree.Groups.Select(g => new RolePermissionGroupDto
        {
            Name = g.Name,
            Enable = g.Enable,
            Permissions = g.Permissions.Select(p => new RolePermissionNodeDto
            {
                Name = p.Name,
                Enable = p.Enable,
                Actions = new Dictionary<string, bool>(p.Actions ?? [], StringComparer.Ordinal),
            }).ToList(),
        }).ToList(),
    };

    private static RolePermission? ToModel(RolePermissionDto? tree) => tree is null ? null : new RolePermission
    {
        Groups = tree.Groups.Select(g => new RolePermissionGroup
        {
            Name = g.Name,
            Enable = g.Enable,
            Permissions = g.Permissions.Select(p => new RolePermissionNode
            {
                Name = p.Name,
                Enable = p.Enable,
                Actions = new Dictionary<string, bool>(p.Actions, StringComparer.Ordinal),
            }).ToList(),
        }).ToList(),
    };
}
