param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9]*$')]
    [string]$Name,

    [string]$OutputPath = "output/crud-modules",

    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$targetRoot = Join-Path $repoRoot $OutputPath
$moduleRoot = Join-Path $targetRoot $Name

if ((Test-Path -LiteralPath $moduleRoot) -and -not $Force) {
    throw "CRUD module scaffold already exists. Use -Force to overwrite: $moduleRoot"
}

if (Test-Path -LiteralPath $moduleRoot) {
    Remove-Item -LiteralPath $moduleRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $moduleRoot | Out-Null

function New-ScaffoldFile {
    param(
        [string]$RelativePath,
        [string]$Content
    )

    $fullPath = Join-Path $moduleRoot $RelativePath
    $directory = Split-Path -Path $fullPath -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    Set-Content -LiteralPath $fullPath -Value $Content -Encoding UTF8
}

New-ScaffoldFile "AccessDatas/Models/$Name.cs" @"
namespace MeetingRecord.AccessDatas.Models;

public class $Name
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Status { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
"@

New-ScaffoldFile "Dtos/Models/${Name}Dto.cs" @"
namespace MeetingRecord.Dtos.Models;

public class ${Name}Dto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
"@

New-ScaffoldFile "Dtos/Models/${Name}CreateUpdateDto.cs" @"
using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Dtos.Models;

public class ${Name}CreateUpdateDto
{
    public int Id { get; set; }

    [Required(ErrorMessage = "名稱不可為空白。")]
    [StringLength(100, ErrorMessage = "名稱不可超過 100 個字。")]
    public string Name { get; set; } = string.Empty;

    public bool Status { get; set; } = true;
}
"@

New-ScaffoldFile "Dtos/Commons/${Name}SearchRequestDto.cs" @"
namespace MeetingRecord.Dtos.Commons;

public class ${Name}SearchRequestDto : SearchRequestBaseDto
{
    public bool? Status { get; set; }
}
"@

New-ScaffoldFile "Business/Repositories/${Name}Repository.cs" @"
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Business.Repositories;

public class ${Name}Repository
{
    private readonly BackendDBContext context;

    public ${Name}Repository(BackendDBContext context)
    {
        this.context = context;
    }

    public Task<$Name?> GetByIdAsync(int id)
    {
        return context.Set<$Name>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<$Name>> GetPagedAsync(${Name}SearchRequestDto request)
    {
        var query = context.Set<$Name>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            query = query.Where(x => x.Name.Contains(request.Keyword));
        }

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(x => x.Id)
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<$Name>
        {
            Items = items,
            TotalCount = totalCount,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize)
        };
    }
}
"@

New-ScaffoldFile "Web/Controllers/${Name}Controller.cs" @"
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class ${Name}Controller : ControllerBase
{
    [HttpPost("search")]
    public ActionResult<ApiResult<PagedResult<${Name}Dto>>> Search([FromBody] ${Name}SearchRequestDto request)
    {
        return Ok(ApiResult<PagedResult<${Name}Dto>>.SuccessResult(new PagedResult<${Name}Dto>(), "搜尋成功"));
    }
}
"@

New-ScaffoldFile "Web/Components/Pages/${Name}Page.razor" @"
@page "/App/$Name"

<${Name}View />
"@

New-ScaffoldFile "Web/Components/Views/${Name}View.razor" @"
<PageHeader Title="$Name" />

<Table TItem="${Name}Dto"
       DataSource="@items"
       Loading="@isLoading"
       RowKey="x => x.Id.ToString()">
    <Column TData="string" Title="名稱" @bind-Field="context.Name" />
</Table>

@code {
    private bool isLoading;
    private readonly List<${Name}Dto> items = [];
}
"@

New-ScaffoldFile "Tests/${Name}ApiTests.cs" @"
namespace MeetingRecord.Tests;

public sealed class ${Name}ApiTests
{
    [Fact]
    public void Scaffold_ShouldBeCompletedBeforeUse()
    {
        Assert.True(true);
    }
}
"@

New-ScaffoldFile "Menu.${Name}.json" @"
{
  "title": "$Name",
  "url": "/App/$Name",
  "permissionName": "$Name"
}
"@

Write-Host "Created CRUD module scaffold at $moduleRoot"
