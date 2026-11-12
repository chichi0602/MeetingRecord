using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Todos;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 待辦事項 API。1.1.18 起改走畫面用的同一套 <see cref="TodoService"/>：資料範圍、封存唯讀、子項目、
/// 只限負責人刪除、甲方只能刪自己建立的、改期限要附原因、異動紀錄與通知，全部只有一份規則。
/// 既有五支（GetById／Search／Create／Update／Delete）的路由與回應形狀不變、只多帶欄位；
/// 畫面專用的形狀另開端點（清單樹、負責人面板、異動紀錄、可選主待辦與專案、勾選完成）。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public class TodoController(
    ILogger<TodoController> logger,
    TodoService todoService,
    ProjectService projectService,
    ProjectAccessService projectAccessService,
    IPermissionChecker permissionChecker,
    IMapper mapper) : ControllerBase
{
    private const string NotFoundMessage = "找不到 ID 為 {0} 的待辦事項";

    /// <summary>與畫面相同的選項（0.4.115）：以前收任意字串，面板各狀態加總對不上，編輯時下拉也會空白。</summary>
    private static string? ValidateBusinessRules(TodoCreateUpdateDto dto)
    {
        if (!TodoAdapterModel.StatusOptions.Contains(dto.Status))
        {
            return $"狀態必須是：{string.Join("、", TodoAdapterModel.StatusOptions)}";
        }

        if (!TodoAdapterModel.PriorityOptions.Contains(dto.Priority))
        {
            return $"優先度必須是：{string.Join("、", TodoAdapterModel.PriorityOptions)}";
        }

        return null;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<TodoDto>>> GetById(int id)
    {
        var model = await todoService.GetAsync(id);
        if (model.Id == 0)
        {
            return NotFound(ApiResult<TodoDto>.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        return Ok(ApiResult<TodoDto>.SuccessResult((await ToDtosAsync([model]))[0], "取得待辦事項成功"));
    }

    /// <summary>對外的平面搜尋（主待辦與子項目一樣逐筆分頁），行為同 1.1.17 以前。</summary>
    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<TodoDto>>>> Search([FromBody] TodoSearchRequestDto request)
    {
        var (items, totalCount) = await todoService.SearchFlatAsync(request);
        return Ok(ApiResult<PagedResult<TodoDto>>.SuccessResult(new PagedResult<TodoDto>
        {
            Items = await ToDtosAsync(items),
            TotalCount = totalCount,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        }, "搜尋待辦事項成功"));
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<TodoDto>>> Create([FromBody] TodoCreateUpdateDto todoDto)
    {
        if (ValidateBusinessRules(todoDto) is { } ruleError)
        {
            return BadRequest(ApiResult<TodoDto>.ValidationError(ruleError));
        }

        // 一律由資料庫配號（0.4.115）：照抄用戶端的 Id 可以拿來試探哪些 Id 存在。
        var model = mapper.Map<TodoAdapterModel>(todoDto);
        model.Id = 0;
        if (await todoService.BeforeAddCheckAsync(model) is { Success: false } check)
        {
            return BadRequest(ApiResult<TodoDto>.ValidationError(check.Message));
        }

        var result = await todoService.AddAsync(model, AuditVia.Api);
        if (!result.Success)
        {
            return BadRequest(ApiResult<TodoDto>.ValidationError(result.Message));
        }

        logger.LogInformation("Todo created through API. TodoId={TodoId}", model.Id);
        return Ok(ApiResult<TodoDto>.SuccessResult((await ToDtosAsync([await todoService.GetAsync(model.Id)]))[0], "新增待辦事項成功"));
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] TodoCreateUpdateDto todoDto)
    {
        if (id != todoDto.Id)
        {
            return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
        }

        if (ValidateBusinessRules(todoDto) is { } ruleError)
        {
            return BadRequest(ApiResult.ValidationError(ruleError));
        }

        if ((await todoService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var model = mapper.Map<TodoAdapterModel>(todoDto);
        if (await todoService.BeforeUpdateCheckAsync(model) is { Success: false } check)
        {
            return BadRequest(ApiResult.ValidationError(check.Message));
        }

        var result = await todoService.UpdateAsync(model, AuditVia.Api);
        return result.Success
            ? Ok(ApiResult.SuccessResult("更新待辦事項成功"))
            : BadRequest(ApiResult.ValidationError(result.Message));
    }

    // 1.0.52：刪除只限專案負責人（服務層把關），負責人是一般使用者也能刪，所以這裡只要求「編輯」，與畫面一致。
    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        var model = await todoService.GetAsync(id);
        if (model.Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        if (await todoService.BeforeDeleteCheckAsync(model) is { Success: false } check)
        {
            return BadRequest(ApiResult.ValidationError(check.Message));
        }

        var result = await todoService.DeleteAsync(id, AuditVia.Api);
        return result.Success
            ? Ok(ApiResult.SuccessResult("刪除待辦事項成功"))
            : BadRequest(ApiResult.ValidationError(result.Message));
    }

    // ---------------- 畫面專用（1.1.18） ----------------

    /// <summary>清單頁：只分頁主層，子項目跟著主待辦；篩選命中子項目時也列出它的主待辦。</summary>
    [HttpPost("tree")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<TodoDto>>>> Tree([FromBody] TodoTreeRequestDto request)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var pageIndex = Math.Max(1, request.PageIndex);
        var result = await todoService.GetAsync(new DataRequest
        {
            Search = request.Keyword ?? string.Empty,
            SortField = request.SortBy ?? string.Empty,
            SortDescending = request.SortDescending,
            CurrentPage = pageIndex,
            PageSize = pageSize,
            ProjectFilter = request.ProjectId is > 0 ? request.ProjectId : null,
            ProjectScope = ParseScope(request.ProjectScope),
            StatusFilter = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status,
        });

        return Ok(ApiResult<PagedResult<TodoDto>>.SuccessResult(new PagedResult<TodoDto>
        {
            Items = await ToDtosAsync(result.Result.ToList()),
            TotalCount = result.Count,
            PageIndex = pageIndex,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)pageSize),
        }));
    }

    /// <summary>勾選完成或取消完成（只寫狀態一個欄位）。</summary>
    [HttpPut("{id}/completed")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> SetCompleted(int id, [FromBody] TodoCompletedRequestDto request)
    {
        if ((await todoService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var result = await todoService.SetCompletedAsync(id, request.IsCompleted, AuditVia.Api);
        return result.Success
            ? Ok(ApiResult.SuccessResult(request.IsCompleted ? "已標為完成" : "已取消完成"))
            : BadRequest(ApiResult.FailureResult(result.Message));
    }

    /// <summary>右側負責人面板：只跟專案與檢視範圍連動（不吃狀態與關鍵字，否則每個人都會變 100% 或 0%）。</summary>
    [HttpGet("owners")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TodoOwnerSummaryDto>>>> Owners([FromQuery] int? projectId, [FromQuery] string? scope)
    {
        var summaries = await todoService.GetOwnerSummariesAsync(projectId, scope: ParseScope(scope));
        return Ok(ApiResult<List<TodoOwnerSummaryDto>>.SuccessResult(summaries.Select(x => new TodoOwnerSummaryDto
        {
            Owner = x.Owner,
            Total = x.Total,
            Completed = x.Completed,
            InProgress = x.InProgress,
            Pending = x.Pending,
            Overdue = x.Overdue,
        }).ToList()));
    }

    /// <summary>某位負責人的待辦（面板的小清單，不分頁）。</summary>
    [HttpGet("by-owner")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TodoDto>>>> ByOwner([FromQuery] string owner, [FromQuery] int? projectId, [FromQuery] string? scope)
    {
        var items = await todoService.GetByOwnerAsync(owner ?? string.Empty, projectId, scope: ParseScope(scope));
        return Ok(ApiResult<List<TodoDto>>.SuccessResult(await ToDtosAsync(items.ToList())));
    }

    /// <summary>異動紀錄（看不到的待辦回空清單，同畫面）。</summary>
    [HttpGet("{id}/changes")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TodoChangeDto>>>> Changes(int id)
    {
        var changes = await todoService.GetChangesAsync(id);
        return Ok(ApiResult<List<TodoChangeDto>>.SuccessResult(changes.Select(x => new TodoChangeDto
        {
            Id = x.Id,
            Field = x.Field,
            OldValue = x.OldValue,
            NewValue = x.NewValue,
            ChangedByName = x.ChangedByName,
            ChangedAt = x.ChangedAt,
            Via = x.Via,
            SourceMeetingId = x.SourceMeetingId,
            SourceMeetingTitle = x.SourceMeetingTitle,
            Note = x.Note,
            FieldText = TodoChangeFields.Describe(x.Field),
            ViaText = TodoChangeFields.DescribeVia(x.Via, x.SourceMeetingTitle),
            OldValueText = TodoChangeRecorder.DisplayValue(x.Field, x.OldValue),
            NewValueText = TodoChangeRecorder.DisplayValue(x.Field, x.NewValue),
        }).ToList()));
    }

    /// <summary>表單「屬於（主待辦）」的選項：新增或修改待辦時用，所以有其中一個權限即可。</summary>
    [HttpGet("parent-candidates")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<List<TodoParentOptionDto>>>> ParentCandidates([FromQuery] int projectId, [FromQuery] int? excludeId)
    {
        var items = projectId > 0 ? await todoService.GetParentCandidatesAsync(projectId, excludeId) : [];
        return Ok(ApiResult<List<TodoParentOptionDto>>.SuccessResult(
            items.Select(x => new TodoParentOptionDto { Id = x.Id, Title = x.Title, ProjectId = x.ProjectId }).ToList()));
    }

    /// <summary>待辦頁看得到的專案（含已完成；篩選與表單用），不借用專案頁的權限。</summary>
    [HttpGet("projects/selectable")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TodoProjectOptionDto>>>> SelectableProjects()
    {
        var projects = await projectService.GetSelectableAsync(includeArchived: true);
        return Ok(ApiResult<List<TodoProjectOptionDto>>.SuccessResult(projects.Select(x => new TodoProjectOptionDto
        {
            Id = x.Id,
            Title = x.Title,
            Status = x.Status,
            IsArchived = x.IsArchived,
        }).ToList()));
    }

    private static ProjectScope ParseScope(string? scope)
        => Enum.TryParse<ProjectScope>(scope, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : ProjectScope.All;

    /// <summary>
    /// 轉成 DTO 並依呼叫者填 <see cref="TodoDto.CanDelete"/>：規則同畫面——專案負責人或管理者（甲方只能刪自己建立的）、
    /// 有待辦的修改權限、專案沒有封存（管理者除外）。子項目一起算。
    /// </summary>
    private async Task<List<TodoDto>> ToDtosAsync(List<TodoAdapterModel> models)
    {
        var dtos = mapper.Map<List<TodoDto>>(models);
        var access = await projectAccessService.GetAsync();
        var canEdit = UserClaims.GetUserId(User) is { } userId
            && await permissionChecker.HasPermissionAsync(userId, PermissionKey.For(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit));

        void Fill(TodoDto dto)
        {
            dto.CanDelete = canEdit
                && access.CanDeleteTodo(dto.ProjectId, dto.CreatedByUserId)
                && (access.IsAdmin || dto.ProjectStatus != ProjectAdapterModel.ArchivedStatus);
            foreach (var child in dto.Children ?? [])
            {
                Fill(child);
            }
        }

        dtos.ForEach(Fill);
        return dtos;
    }
}
