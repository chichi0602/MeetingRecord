# 產生一組 CRUD 模組的骨架（WASM 頁面＋API＋服務＋授權測試），直接寫進方案的各專案。
#
# 產生的檔案（<Name> 是英文的實體名稱，例：Product）：
#   AccessDatas/Models/<Name>.cs                         資料表實體
#   Dtos/Models/<Name>CreateUpdateDto.cs、<Name>Dto.cs     API 的輸入與輸出（Dto 有 Clone()，頁面編輯前先複製）
#   Dtos/Commons/<Name>SearchRequestDto.cs               搜尋條件（分頁、排序、關鍵字、啟用篩選）
#   Business/Services/DataAccess/<Name>Service.cs        規則與資料存取；Update／Delete 前以 CleanTrackingHelper 清除追蹤
#   Web/Controllers/<Name>Controller.cs                  api/v1/<Name>：ApiScheme＋[HasPermission]，回傳 ApiResult<T>／PagedResult<T>
#   Web.Client/Api/<Name>Api.cs                          WASM 端呼叫 API 的包裝
#   Web.Client/Pages/<Name>/<Name>Page.razor(.cs/.css)   清單＋新增／修改對話窗；權限用 PermissionView，擋下時記稽核
#   Tests/<Name>ApiTests.cs                              L2：未登入 401、沒權限 403、有權限 CRUD 走一輪
#
# 產生後還要手動登錄 8 個地方（腳本最後會列出要貼的程式碼）：權限鍵常數、權限目錄、Menu.json、MenuPermissionMap、
# DbSet＋migration、服務與 API 的 DI 註冊、ClientJsonContext。完整說明見 docs/guides/建立一個新 CRUD 操作網頁說明.md。
#
# 用法：
#   pwsh scripts/New-CrudModule.ps1 -Name Product -Title 產品清單
#   pwsh scripts/New-CrudModule.ps1 -Name Product -Title 產品清單 -Route /products -Force
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Z][A-Za-z0-9]*$')]
    [string]$Name,

    # 頁面標題，同時是權限鍵（MagicObjectHelper.角色_<Title>）與選單名稱。
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[\p{L}\p{N}]+$')]
    [string]$Title,

    # 頁面路由，預設 /<name 小寫>。
    [ValidatePattern('^/[a-z0-9\-/]+$')]
    [string]$Route,

    # 方案資料夾（含各專案的那一層），預設 <repo>/src/MeetingRecord。
    [string]$SolutionRoot,

    # 檔案已存在時覆寫。
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($SolutionRoot)) { $SolutionRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "src/MeetingRecord" }
$SolutionRoot = (Resolve-Path $SolutionRoot).Path
if (-not (Test-Path (Join-Path $SolutionRoot "MeetingRecord.Web.Client"))) { throw "找不到 $SolutionRoot/MeetingRecord.Web.Client，請用 -SolutionRoot 指定方案資料夾。" }
if ([string]::IsNullOrEmpty($Route)) { $Route = "/" + $Name.ToLowerInvariant() }

$css = $Name.ToLowerInvariant()
$camel = $Name.Substring(0, 1).ToLowerInvariant() + $Name.Substring(1)

$targets = [ordered]@{}
function Add-Template([string]$relativePath, [string]$template) {
    $targets[$relativePath] = $template.Replace("__NAME__", $Name).Replace("__CAMEL__", $camel).Replace("__TITLE__", $Title).Replace("__ROUTE__", $Route).Replace("__CSS__", $css)
}

Add-Template "MeetingRecord.AccessDatas/Models/$Name.cs" @'
using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>__TITLE__</summary>
public class __NAME__
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
'@

Add-Template "MeetingRecord.Dtos/Models/${Name}CreateUpdateDto.cs" @'
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>__TITLE__：新增與修改的輸入。</summary>
public class __NAME__CreateUpdateDto
{
    /// <summary>新增時給 0；修改時要與路由的 id 一致。</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [Required(ErrorMessage = "名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;
}
'@

Add-Template "MeetingRecord.Dtos/Models/${Name}Dto.cs" @'
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>__TITLE__：API 的輸出，也是頁面表單綁定的模型。</summary>
public class __NAME__Dto : __NAME__CreateUpdateDto
{
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>編輯前複製一份再綁表單，取消時不影響清單上的那筆。有集合欄位時要一併複製。</summary>
    public __NAME__Dto Clone() => (__NAME__Dto)MemberwiseClone();
}
'@

Add-Template "MeetingRecord.Dtos/Commons/${Name}SearchRequestDto.cs" @'
namespace MeetingRecord.Dtos.Commons;

/// <summary>__TITLE__的搜尋條件。SortBy 認得 name、isenabled、updatedat、createdat（不分大小寫）。</summary>
public class __NAME__SearchRequestDto : SearchRequestBaseDto
{
    /// <summary>只取啟用（true）或停用（false）的；null 不篩選。</summary>
    public bool? IsEnabled { get; set; }
}
'@

Add-Template "MeetingRecord.Business/Services/DataAccess/${Name}Service.cs" @'
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// __TITLE__的規則與資料存取。API Controller 只呼叫這裡，規則不要寫在 Controller 或頁面。
/// <para>
/// TODO（依需求）：
/// - 資料若屬於專案（要依團隊決定誰看得到），查詢與寫入一律經 <c>ProjectAccessService</c>，看不到的當成不存在；
/// - 要留稽核就注入 <c>IAuditLogService</c>，在 <c>AuditActions</c> 加動作常數，寫入成功後呼叫 <c>WriteForCurrentUserAsync</c>。
/// </para>
/// </summary>
public class __NAME__Service
{
    private readonly BackendDBContext context;
    private readonly ILogger<__NAME__Service> logger;

    public __NAME__Service(BackendDBContext context, ILogger<__NAME__Service> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public async Task<PagedResult<__NAME__Dto>> SearchAsync(__NAME__SearchRequestDto request)
    {
        var pageIndex = Math.Max(1, request.PageIndex);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        IQueryable<__NAME__> query = context.Set<__NAME__>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            query = query.Where(x => x.Name.Contains(request.Keyword) || (x.Description != null && x.Description.Contains(request.Keyword)));
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == request.IsEnabled.Value);
        }

        var descending = request.SortDescending;
        query = request.SortBy?.ToLowerInvariant() switch
        {
            "name" => descending ? query.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id) : query.OrderBy(x => x.Name).ThenBy(x => x.Id),
            "isenabled" => descending ? query.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.Id) : query.OrderBy(x => x.IsEnabled).ThenBy(x => x.Id),
            "updatedat" => descending ? query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id) : query.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id),
            "createdat" => descending ? query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id) : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id),
        };

        var total = await query.CountAsync();
        var items = await query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedResult<__NAME__Dto>
        {
            Items = items.Select(ToDto).ToList(),
            TotalCount = total,
            PageIndex = pageIndex,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize),
        };
    }

    /// <returns>找不到時回傳 null。</returns>
    public async Task<__NAME__Dto?> GetAsync(int id)
    {
        var item = await context.Set<__NAME__>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? null : ToDto(item);
    }

    /// <summary>名稱是否已被其他筆使用（去頭尾空白、不分大小寫）。</summary>
    public Task<bool> IsNameTakenAsync(string name, int exceptId)
    {
        var normalized = name.Trim().ToLower();
        return context.Set<__NAME__>().AsNoTracking().AnyAsync(x => x.Id != exceptId && x.Name.ToLower() == normalized);
    }

    /// <summary>新增；成功時把新的編號寫回 <paramref name="dto"/>.Id。</summary>
    public async Task<VerifyRecordResult> AddAsync(__NAME__CreateUpdateDto dto)
    {
        try
        {
            CleanTrackingHelper.Clean<__NAME__>(context);
            var item = new __NAME__
            {
                Name = dto.Name.Trim(),
                Description = dto.Description,
                IsEnabled = dto.IsEnabled,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };
            context.Set<__NAME__>().Add(item);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<__NAME__>(context);
            dto.Id = item.Id;
            logger.LogInformation("__NAME__ created. Id={Id}, Name={Name}", item.Id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create __NAME__. Name={Name}", dto.Name);
            return VerifyRecordResultFactory.Build(false, "新增失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(__NAME__CreateUpdateDto dto)
    {
        try
        {
            CleanTrackingHelper.Clean<__NAME__>(context);
            var item = await context.Set<__NAME__>().FirstOrDefaultAsync(x => x.Id == dto.Id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要修改的資料。");
            }

            item.Name = dto.Name.Trim();
            item.Description = dto.Description;
            item.IsEnabled = dto.IsEnabled;
            item.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<__NAME__>(context);
            logger.LogInformation("__NAME__ updated. Id={Id}", dto.Id);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update __NAME__. Id={Id}", dto.Id);
            return VerifyRecordResultFactory.Build(false, "修改失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        try
        {
            CleanTrackingHelper.Clean<__NAME__>(context);
            var item = await context.Set<__NAME__>().FirstOrDefaultAsync(x => x.Id == id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的資料。");
            }

            context.Set<__NAME__>().Remove(item);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<__NAME__>(context);
            logger.LogInformation("__NAME__ deleted. Id={Id}", id);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete __NAME__. Id={Id}", id);
            return VerifyRecordResultFactory.Build(false, "刪除失敗。", ex);
        }
    }

    private static __NAME__Dto ToDto(__NAME__ item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Description = item.Description,
        IsEnabled = item.IsEnabled,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };
}
'@

Add-Template "MeetingRecord.Web/Controllers/${Name}Controller.cs" @'
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// __TITLE__ API。網頁（WASM）用登入 Cookie 呼叫、外部程式用 JWT（<see cref="MagicObjectHelper.ApiScheme"/>）；
/// 每個動作以 <see cref="HasPermissionAttribute"/> 檢查角色的動作權限（沒權限 403），規則都在 <see cref="__NAME__Service"/>。
/// </summary>
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public class __NAME__Controller : ControllerBase
{
    private readonly ILogger<__NAME__Controller> logger;
    private readonly __NAME__Service service;

    public __NAME__Controller(ILogger<__NAME__Controller> logger, __NAME__Service service)
    {
        this.logger = logger;
        this.service = service;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色___TITLE__, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<__NAME__Dto>>> GetById(int id)
    {
        try
        {
            var item = await service.GetAsync(id);
            return item is null
                ? NotFound(ApiResult<__NAME__Dto>.NotFoundResult($"找不到 ID 為 {id} 的資料"))
                : Ok(ApiResult<__NAME__Dto>.SuccessResult(item, "取得資料成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get __NAME__. Id={Id}", id);
            return this.ApiServerError<__NAME__Dto>("取得資料失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色___TITLE__, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<__NAME__Dto>>>> Search([FromBody] __NAME__SearchRequestDto request)
    {
        try
        {
            return Ok(ApiResult<PagedResult<__NAME__Dto>>.SuccessResult(await service.SearchAsync(request), "搜尋成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search __NAME__");
            return this.ApiServerError<PagedResult<__NAME__Dto>>("搜尋失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色___TITLE__, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<__NAME__Dto>>> Create([FromBody] __NAME__CreateUpdateDto dto)
    {
        try
        {
            dto.Id = 0;
            if (await service.IsNameTakenAsync(dto.Name, 0))
            {
                return Conflict(ApiResult<__NAME__Dto>.ConflictResult("名稱已存在，無法新增。"));
            }

            var added = await service.AddAsync(dto);
            if (!added.Success)
            {
                return BadRequest(ApiResult<__NAME__Dto>.FailureResult(added.Message));
            }

            return Ok(ApiResult<__NAME__Dto>.SuccessResult((await service.GetAsync(dto.Id))!, "新增成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create __NAME__. Name={Name}", dto.Name);
            return this.ApiServerError<__NAME__Dto>("新增失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色___TITLE__, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] __NAME__CreateUpdateDto dto)
    {
        try
        {
            if (id != dto.Id)
            {
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (await service.GetAsync(id) is null)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的資料"));
            }

            if (await service.IsNameTakenAsync(dto.Name, id))
            {
                return Conflict(ApiResult.ConflictResult("名稱已存在，無法修改。"));
            }

            var updated = await service.UpdateAsync(dto);
            return updated.Success ? Ok(ApiResult.SuccessResult("修改成功")) : BadRequest(ApiResult.FailureResult(updated.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update __NAME__. Id={Id}", id);
            return this.ApiServerError("修改失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色___TITLE__, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            if (await service.GetAsync(id) is null)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的資料"));
            }

            var deleted = await service.DeleteAsync(id);
            return deleted.Success ? Ok(ApiResult.SuccessResult("刪除成功")) : BadRequest(ApiResult.FailureResult(deleted.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete __NAME__. Id={Id}", id);
            return this.ApiServerError("刪除失敗", ex);
        }
    }
}
'@

Add-Template "MeetingRecord.Web.Client/Api/${Name}Api.cs" @'
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;

namespace MeetingRecord.Web.Client.Api;

/// <summary>__TITLE__：<c>api/v1/__NAME__</c>。規則都在伺服器（__NAME__Service），這裡只轉呼叫。</summary>
public sealed class __NAME__Api(ApiClient api)
{
    private const string BaseUrl = "api/v1/__NAME__";

    public Task<ApiResult<PagedResult<__NAME__Dto>>> SearchAsync(__NAME__SearchRequestDto request)
        => api.PostAsync($"{BaseUrl}/search", request, ClientJsonContext.Default.__NAME__SearchRequestDto, ClientJsonContext.Default.__NAME__PageResult);

    public Task<ApiResult<__NAME__Dto>> CreateAsync(__NAME__CreateUpdateDto item)
        => api.PostAsync(BaseUrl, item, ClientJsonContext.Default.__NAME__CreateUpdateDto, ClientJsonContext.Default.__NAME__Result);

    public Task<ApiResult<object>> UpdateAsync(__NAME__CreateUpdateDto item)
        => api.PutAsync($"{BaseUrl}/{item.Id}", item, ClientJsonContext.Default.__NAME__CreateUpdateDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);
}
'@

Add-Template "MeetingRecord.Web.Client/Pages/$Name/${Name}Page.razor" @'
@page "__ROUTE__"
@using MeetingRecord.Dtos.Models

<PageTitle>__TITLE__</PageTitle>

@if (string.IsNullOrEmpty(RoleMessage) == false)
{
    <div class="alert alert-danger" role="alert">
        @RoleMessage
    </div>
}
else
{
    <div>
        <div class="__CSS__-toolbar">
            <div class="__CSS__-toolbar-left">
                @if (PermissionView.Can(MagicObjectHelper.角色___TITLE__, PermissionActions.Create))
                {
                    <HoverTip Title="新增">
                        <Button Type="ButtonType.Default" OnClick="@(async () => await OnAddAsync())">
                            <span class="btn-icon-label"><MaterialIcon Theme="MaterialIcon.Themes.Outlined" Kind="add" /><span>新增</span></span>
                        </Button>
                    </HoverTip>
                }
                <HoverTip Title="重新整理">
                    <Button Type="ButtonType.Default" OnClick="@(async () => await ReloadAsync())">
                        <span class="btn-icon-label"><MaterialIcon Theme="MaterialIcon.Themes.Outlined" Kind="refresh" /><span>重新整理</span></span>
                    </Button>
                </HoverTip>
            </div>
            <div class="__CSS__-toolbar-right">
                <Input class="__CSS__-search-input" @bind-Value="searchText" BindOnInput="true" DebounceMilliseconds="0" OnPressEnter="@(async () => await OnSearchAsync())" Placeholder="請輸入搜尋關鍵字" />
                <HoverTip Title="搜尋">
                    <Button Type="ButtonType.Default" OnClick="@(async () => await OnSearchAsync())">
                        <span class="btn-icon-label"><MaterialIcon Theme="MaterialIcon.Themes.Outlined" Kind="search" /><span>搜尋</span></span>
                    </Button>
                </HoverTip>
            </div>
        </div>
        <Table TItem="__NAME__Dto"
               RemoteDataSource="true"
               DataSource="@items"
               RowKey="x => x.Id"
               OnChange="OnTableChange"
               @bind-PageIndex="_pageIndex"
               @bind-PageSize="_pageSize"
               @bind-Total="_total">
            <Column TData="string" DataIndex="@nameof(__NAME__Dto.Name)" Title="名稱" Sortable ShowSorterTooltip="false" />
            <Column TData="string" DataIndex="@nameof(__NAME__Dto.Description)" Title="描述" />
            <Column TData="bool" DataIndex="@nameof(__NAME__Dto.IsEnabled)" Title="啟用狀態" Sortable ShowSorterTooltip="false">
                @(context.IsEnabled ? "啟用" : "停用")
            </Column>
            <Column TData="DateTime" DataIndex="@nameof(__NAME__Dto.UpdatedAt)" Title="更新時間" Sortable ShowSorterTooltip="false" DefaultSortOrder="SortDirection.Descending" />
            <ActionColumn Fixed="ColumnFixPlacement.Right" Title="操作">
                <Space>
                    @if (PermissionView.Can(MagicObjectHelper.角色___TITLE__, PermissionActions.Edit))
                    {
                        <SpaceItem>
                            <CrudActionButton Title="修改" Icon="edit" OnClick="@(async () => await OnEditAsync(context))" />
                        </SpaceItem>
                    }
                    @if (PermissionView.Can(MagicObjectHelper.角色___TITLE__, PermissionActions.Delete))
                    {
                        <SpaceItem>
                            <CrudActionButton Title="刪除" Icon="delete" Danger OnClick="@(async () => await OnDeleteAsync(context))" />
                        </SpaceItem>
                    }
                </Space>
            </ActionColumn>
        </Table>
    </div>

    @* 對話窗用單向繫結：開關一律由本頁決定，伺服器擋下時視窗與填的內容都留著。 *@
    <Modal Title="@modalTitle"
           Class="form-modal-compact"
           Visible="@modalVisible"
           Keyboard="true"
           OnOk="OnModalOKHandleAsync"
           OnCancel="OnModalCancelHandleAsync">
        <EditForm Model="@CurrentRecord" Context="editContext" @onkeydown="OnModalKeyDownAsync">
            <DataAnnotationsValidator />
            <ValidationSummary />
            <InputWatcher EditContextActionChanged="OnEditContextChanged" />

            <AntDesign.Form TModel="__NAME__Dto" Model="@CurrentRecord" Layout="FormLayout.Vertical" Context="formContext">
                <div class="form-modal-grid">
                    <FormItem Label="名稱" Required>
                        <Input @bind-Value="CurrentRecord.Name" Placeholder="請輸入名稱" />
                        <ValidationMessage For="() => CurrentRecord.Name" />
                    </FormItem>
                    <FormItem Label="啟用狀態">
                        <Switch @bind-Value="CurrentRecord.IsEnabled" CheckedChildren="啟用" UnCheckedChildren="停用" />
                    </FormItem>
                    <FormItem Label="描述" Class="form-modal-full">
                        <TextArea @bind-Value="CurrentRecord.Description" Rows="4" Placeholder="請輸入描述" />
                        <ValidationMessage For="() => CurrentRecord.Description" />
                    </FormItem>
                </div>
            </AntDesign.Form>
        </EditForm>
    </Modal>
}
'@

Add-Template "MeetingRecord.Web.Client/Pages/$Name/${Name}Page.razor.cs" @'
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Client.Pages.__NAME__
{
    /// <summary>__TITLE__（WASM）。資料一律經 <see cref="__NAME__Api"/>，名稱重複等規則在伺服器判斷，這裡只顯示結果。</summary>
    public partial class __NAME__Page
    {
        [Inject] private ILogger<__NAME__Page> logger { get; set; } = default!;
        [Inject] private __NAME__Api __NAME__Api { get; set; } = default!;
        [Inject] private PermissionView PermissionView { get; set; } = default!;
        [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
        [Inject] private ModalService modalService { get; set; } = default!;
        [Inject] private NotificationService notificationService { get; set; } = default!;

        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";
        List<__NAME__Dto> items = new();

        string modalTitle = "__TITLE__";
        bool modalVisible;
        bool isNewRecordMode;
        __NAME__Dto CurrentRecord = NewRecord();
        EditContext? LocalEditContext;
        string RoleMessage = string.Empty;

        /// <summary>開啟表單當下的快照；null 代表還沒開過。</summary>
        private string? formSnapshot;

        /// <summary>Esc 會同時觸發 Modal 的 Keyboard 與表單的 @onkeydown，沒有這道旗標會疊出兩個確認視窗。</summary>
        private bool isDiscardConfirming;

        /// <summary>「確定」與 Enter 共用的送出閘門，連按不會新增兩筆。</summary>
        private readonly FormSubmitGate submitGate = new();

        protected override async Task OnInitializedAsync()
        {
            // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過。
            await PermissionView.LoadAsync();
            if (!PermissionView.CanAccessPage(MagicObjectHelper.角色___TITLE__))
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await PageAccessApi.RecordPageDeniedAsync("__ROUTE__");
                logger.LogWarning("__NAME__ page denied because current user has not this role permission.");
                return;
            }

            await ReloadAsync();
        }

        private static __NAME__Dto NewRecord() => new() { Id = 0, IsEnabled = true };

        public async Task ReloadAsync()
        {
            var result = await __NAME__Api.SearchAsync(new __NAME__SearchRequestDto
            {
                Keyword = searchText,
                SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
                SortDescending = sortDirection == "Descending",
                PageIndex = _pageIndex,
                PageSize = _pageSize,
            });

            if (!result.Success || result.Data is not { } page)
            {
                ShowError(result);
                return;
            }

            items = page.Items;
            _total = page.TotalCount;
            // 頁碼落在最後一頁之後（例如刪掉該頁唯一一筆）：夾回最後一頁重載一次。
            if (items.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                await ReloadAsync();
                return;
            }

            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<__NAME__Dto> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;
            var sorted = args.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
            sortField = sorted?.FieldName ?? string.Empty;
            sortDirection = sorted?.SortDirection.ToString() ?? "None";
            await ReloadAsync();
        }

        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            await ReloadAsync();
        }

        Task OnAddAsync()
        {
            isNewRecordMode = true;
            modalTitle = "新增__TITLE__";
            CurrentRecord = NewRecord();
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
            return Task.CompletedTask;
        }

        Task OnEditAsync(__NAME__Dto item)
        {
            isNewRecordMode = false;
            modalTitle = "修改__TITLE__";
            // 先複製一份再綁表單：取消時清單上的那筆不受影響。
            CurrentRecord = item.Clone();
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
            return Task.CompletedTask;
        }

        async Task OnDeleteAsync(__NAME__Dto item)
        {
            var ok = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "確認刪除",
                Content = "確定要刪除這筆紀錄嗎？此操作無法復原。",
                OkText = "刪除",
                CancelText = "取消",
                OkButtonProps = new ButtonProps { Danger = true },
                MaskClosable = false,
            });
            if (!ok)
            {
                return;
            }

            var result = await __NAME__Api.DeleteAsync(item.Id);
            if (!result.Success)
            {
                ShowError(result);
                return;
            }

            Notify("刪除成功", NotificationType.Success);
            await ReloadAsync();
        }

        private Task OnModalOKHandleAsync(MouseEventArgs args) => submitGate.RunAsync(SaveModalAsync);

        private async Task SaveModalAsync()
        {
            // 視窗已經關了就不再存（關窗動畫期間「確定」還按得到）。
            if (!modalVisible)
            {
                return;
            }

            if (LocalEditContext?.Validate() == false)
            {
                foreach (var error in LocalEditContext.GetValidationMessages())
                {
                    Notify(error, NotificationType.Error, "驗證失敗");
                }

                return;
            }

            // 名稱重複（409）、找不到（404）由伺服器判斷；失敗時表單留著。
            var saved = isNewRecordMode
                ? Succeeded(await __NAME__Api.CreateAsync(CurrentRecord))
                : Succeeded(await __NAME__Api.UpdateAsync(CurrentRecord));
            if (!saved)
            {
                return;
            }

            Notify(isNewRecordMode ? "新增成功" : "修改成功", NotificationType.Success);
            await ReloadAsync();
            modalVisible = false;
            formSnapshot = null;
        }

        private async Task OnModalCancelHandleAsync(MouseEventArgs args)
        {
            if (isDiscardConfirming)
            {
                return;
            }

            if (FormDirtyHelper.IsDirty(formSnapshot, CurrentRecord))
            {
                isDiscardConfirming = true;
                bool discard;
                try
                {
                    discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這筆資料");
                }
                finally
                {
                    isDiscardConfirming = false;
                }

                if (!discard)
                {
                    return;
                }
            }

            modalVisible = false;
            formSnapshot = null;
        }

        private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
        {
            // 一律走 FormKeyboardHelper：擋掉中文輸入法選字的 Enter，Shift+Enter 照常換行。
            if (FormKeyboardHelper.IsSubmit(args))
            {
                // AntDesign Input 在 change/blur 才回寫繫結值，Enter 送出前要等它回寫。
                await submitGate.RunAsync(SaveModalAsync, FormSubmitGate.EnterSettleDelay);
            }
            else if (FormKeyboardHelper.IsCancel(args))
            {
                await OnModalCancelHandleAsync(new MouseEventArgs());
            }
        }

        public void OnEditContextChanged(EditContext context) => LocalEditContext = context;

        private bool Succeeded<T>(ApiResult<T> result)
        {
            if (!result.Success)
            {
                ShowError(result);
            }

            return result.Success;
        }

        private void ShowError<T>(ApiResult<T> result)
        {
            if (result.StatusCode == 401)
            {
                return; // ApiClient 已導到登入頁。
            }

            // API 的失敗原因在 ErrorMessage（Message 只是「操作失敗」之類的通稱）。
            Notify(result.ErrorMessage ?? result.Message, NotificationType.Error);
        }

        private void Notify(string description, NotificationType type, string message = "系統訊息")
            => _ = notificationService.Open(new NotificationConfig
            {
                Message = message,
                Description = description,
                NotificationType = type,
                Placement = NotificationPlacement.BottomRight,
            });
    }
}
'@

Add-Template "MeetingRecord.Web.Client/Pages/$Name/${Name}Page.razor.css" @'
.__CSS__-toolbar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    margin-bottom: 12px;
}

.__CSS__-toolbar-left,
.__CSS__-toolbar-right {
    display: flex;
    align-items: center;
    gap: 8px;
}

.__CSS__-search-input {
    width: 260px;
}
'@

Add-Template "MeetingRecord.Tests/${Name}ApiTests.cs" @'
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// __TITLE__ API（L2）：未登入 401、沒有這頁權限 403、有權限時新增→搜尋→修改→刪除走一輪。
/// 用 WASM 前端的路徑（登入 Cookie＋防偽標頭）呼叫。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class __NAME__ApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "__CAMEL__-api-pass";
    private const string BaseUrl = "/api/v1/__NAME__";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", $"{BaseUrl}/1" },
        { "POST", $"{BaseUrl}/search" },
        { "POST", BaseUrl },
        { "PUT", $"{BaseUrl}/1" },
        { "DELETE", $"{BaseUrl}/1" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Anonymous_ShouldReturn401(string method, string url)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");

        using var response = await SendAsync(client, method, url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithoutPagePermission_ShouldReturn403(string method, string url)
    {
        // 有別頁的權限、沒有這一頁的。
        using var client = await LoginAsync([MagicObjectHelper.角色_使用說明]);

        using var response = await SendAsync(client, method, url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Crud_WithPagePermission_ShouldWork()
    {
        using var client = await LoginAsync([MagicObjectHelper.角色___TITLE__]);
        var name = $"測試-{Guid.NewGuid():N}";

        __NAME__Dto created;
        using (var response = await client.PostAsJsonAsync(BaseUrl, new __NAME__CreateUpdateDto { Name = name }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            created = (await ReadAsync<__NAME__Dto>(response)).Data!;
        }

        using (var duplicate = await client.PostAsJsonAsync(BaseUrl, new __NAME__CreateUpdateDto { Name = $" {name.ToUpperInvariant()} " }))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var search = await client.PostAsJsonAsync($"{BaseUrl}/search", new __NAME__SearchRequestDto { Keyword = name }))
        {
            Assert.Equal([created.Id], (await ReadAsync<PagedResult<__NAME__Dto>>(search)).Data!.Items.Select(x => x.Id));
        }

        using (var update = await client.PutAsJsonAsync($"{BaseUrl}/{created.Id}", new __NAME__CreateUpdateDto { Id = created.Id, Name = name, Description = "改描述" }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        Assert.Equal("改描述", (await ReadAsync<__NAME__Dto>(await client.GetAsync($"{BaseUrl}/{created.Id}"))).Data!.Description);

        using (var delete = await client.DeleteAsync($"{BaseUrl}/{created.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        using var missing = await client.GetAsync($"{BaseUrl}/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url)
    {
        var body = new __NAME__CreateUpdateDto { Id = 1, Name = "x" };
        return method switch
        {
            "GET" => client.GetAsync(url),
            "POST" => client.PostAsJsonAsync(url, body),
            "PUT" => client.PutAsJsonAsync(url, body),
            "DELETE" => client.DeleteAsync(url),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
    }

    private async Task<HttpClient> LoginAsync(string[] permissions)
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, permissions);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
'@

# 先檢查全部，有任何一個已存在又沒帶 -Force 就整組不寫。
$existing = @($targets.Keys | Where-Object { Test-Path (Join-Path $SolutionRoot $_) })
if ($existing.Count -gt 0 -and -not $Force) {
    throw "下列檔案已存在，要覆寫請加 -Force：`n  " + ($existing -join "`n  ")
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
foreach ($relative in $targets.Keys) {
    $full = Join-Path $SolutionRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $full) | Out-Null
    [System.IO.File]::WriteAllText($full, ($targets[$relative] -replace "`r?`n", "`r`n"), $utf8)
    Write-Host "已產生 $relative"
}

$menuId = "<未使用的 id>"
Write-Host @"

接著手動登錄下列 8 處（都在 $SolutionRoot 下），完成後建置並跑測試：

1. MeetingRecord.Share/Helpers/MagicObjectHelper.cs（權限鍵常數，放在其他「角色_」常數旁）
       public const string 角色_$Title = "$Title";

2. MeetingRecord.Business/Services/Other/RolePermissionService.cs 的 GetRoleListPermissionAllName()
   把 MagicObjectHelper.角色_$Title 放進合適的群組（例如「資料定義」），角色管理才勾得到。

3. MeetingRecord.Web/Datas/Menu.json（選單節點；id 全檔唯一，icon 要在 MenuIconTests 的允許清單裡）
       { "id": $menuId, "name": "$Title", "icon": "article", "url": "$Route" }

4. MeetingRecord.Web/Components/Layout/SidebarMenuService.cs 的 MenuPermissionMap
       [$menuId] = MagicObjectHelper.角色_$Title,

5. MeetingRecord.AccessDatas/BackendDBContext.cs 加 DbSet，再產生 migration（本系統只支援 SQLite）
       public virtual DbSet<$Name> $Name { get; set; }
       dotnet ef migrations add Add$Name --project MeetingRecord.AccessDatas --startup-project MeetingRecord.Web

6. MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs（其他 DataAccess 服務旁）
       services.AddScoped<${Name}Service>();

7. MeetingRecord.Web.Client/Program.cs（其他 Api 旁）
       builder.Services.AddScoped<${Name}Api>();

8. MeetingRecord.Web.Client/Api/ClientJsonContext.cs（WASM 端的 JSON 一律走原始碼產生器）
       [JsonSerializable(typeof(ApiResult<PagedResult<${Name}Dto>>), TypeInfoPropertyName = "${Name}PageResult")]
       [JsonSerializable(typeof(ApiResult<${Name}Dto>), TypeInfoPropertyName = "${Name}Result")]
       [JsonSerializable(typeof(${Name}SearchRequestDto))]
       [JsonSerializable(typeof(${Name}CreateUpdateDto))]

驗證：dotnet build -c Release，dotnet test --filter "FullyQualifiedName~${Name}ApiTests"，再跑全部單元測試（頁面慣例的守門測試會掃新頁面）。
"@
