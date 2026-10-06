using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Models.Systems;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Web;
using MeetingRecord.Web.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetingRecord.Tests;

public sealed class ApiIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApiTestApplicationFactory factory;

    public ApiIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ProtectedCrudApi_WithoutBearerToken_ShouldReturnApiResult401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Project/1");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task ProtectedCrudApi_WithoutRequiredPermission_ShouldReturnApiResult403()
    {
        var account = $"limited-{Guid.NewGuid():N}";
        const string password = "limited-pass";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var role = new RoleView
            {
                Name = $"受限角色-{Guid.NewGuid():N}",
                TabViewJson = JsonSerializer.Serialize(new[] { "會議紀錄" }),
            };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();

            db.MyUser.Add(new MyUser
            {
                Account = account,
                Name = "limited",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = account,
            Password = password,
        });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        var response = await client.GetAsync("/api/Project/1");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task ViewOnlyRole_CanRead_ButCannotCreate()
    {
        var account = $"viewer-{Guid.NewGuid():N}";
        const string password = "viewer-pass";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var writer = scope.ServiceProvider.GetRequiredService<MeetingRecord.Business.Services.Other.IRbacWriteService>();

            var role = new RoleView
            {
                Name = $"唯讀-{Guid.NewGuid():N}",
                TabViewJson = JsonSerializer.Serialize(new[] { "專案項目:view" }),
            };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(role.Id, new[] { "專案項目:view" });

            var user = new MyUser
            {
                Account = account,
                Name = "viewer",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { role.Id });
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        // 有 view：讀取被允許（不存在的 id → 404，而非 403）
        var readResponse = await client.GetAsync("/api/Project/999999");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        // 無 create：新增被拒（403）
        var createResponse = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = "should be forbidden",
            Status = "進行中",
            Owner = "viewer",
        });
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
    }

    [Fact]
    public async Task ProjectApi_Create_ShouldRejectMissingDatesOwnerAndBadRules()
    {
        // 0.4.114：日期原本是不可為 null 的 DateTime，沒帶時存成 0001/01/01；負責人沒帶會在 SaveChanges 爆 500。
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);
        var teamId = await SeedTeamAsync();

        var noDates = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0, Title = $"沒日期-{Guid.NewGuid():N}", Status = "進行中", Owner = "x", PrimaryTeamId = teamId,
        });
        var noOwner = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0, Title = $"沒負責人-{Guid.NewGuid():N}", StartDate = DateTime.Today, EndDate = DateTime.Today,
            Status = "進行中", PrimaryTeamId = teamId,
        });
        var badStatus = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0, Title = $"亂狀態-{Guid.NewGuid():N}", StartDate = DateTime.Today, EndDate = DateTime.Today,
            Status = "不存在的狀態", Owner = "x", PrimaryTeamId = teamId,
        });
        var endBeforeStart = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0, Title = $"日期顛倒-{Guid.NewGuid():N}", StartDate = DateTime.Today, EndDate = DateTime.Today.AddDays(-1),
            Status = "進行中", Owner = "x", PrimaryTeamId = teamId,
        });
        // 帶一個不存在的 Id：要由資料庫配號，不能照抄（原本會用這個 Id 寫入）。
        var forcedId = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 987654, Title = $"指定Id-{Guid.NewGuid():N}", StartDate = DateTime.Today, EndDate = DateTime.Today,
            Status = "進行中", Owner = "x", PrimaryTeamId = teamId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, noDates.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noOwner.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, endBeforeStart.StatusCode);
        var created = await ReadApiResultAsync<ProjectDto>(forcedId);
        Assert.True(created.Success);
        Assert.NotEqual(987654, created.Data!.Id);
    }

    [Fact]
    public async Task TodoApi_Create_WithMeetingFromAnotherProject_ShouldReturnBadRequest()
    {
        // 0.4.114：來源會議要屬於同一個專案；原本只檢查專案，可以把別的專案（甚至看不到的）會議掛上來。
        int projectAId;
        int meetingInBId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var team = new Team { Name = $"待辦團隊-{Guid.NewGuid():N}", IsEnabled = true };
            var projectA = new Project { Title = $"A-{Guid.NewGuid():N}", Status = "進行中", Owner = "x" };
            var projectB = new Project { Title = $"B-{Guid.NewGuid():N}", Status = "進行中", Owner = "x" };
            projectA.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
            projectB.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
            db.Project.AddRange(projectA, projectB);
            await db.SaveChangesAsync();
            var meeting = new Meeting { Title = "B 的會議", ProjectId = projectB.Id };
            db.Meeting.Add(meeting);
            await db.SaveChangesAsync();
            projectAId = projectA.Id;
            meetingInBId = meeting.Id;
        }

        using var client = factory.CreateClient();
        await AuthorizeAsync(client);
        var response = await client.PostAsJsonAsync("/api/Todo", new TodoCreateUpdateDto
        {
            Id = 0, Title = "掛錯會議的待辦", ProjectId = projectAId, MeetingId = meetingInBId, Priority = "中", Status = "待辦",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProjectApi_ViewerPreset_CanRead_ButCannotCreateEditDelete()
    {
        // 0.4.109：預設角色「檢視者」配給使用者後，限制要真的生效——讀得到、改不了（API 回 403）。
        var account = $"viewer-{Guid.NewGuid():N}";
        const string password = "viewer-pass";
        int projectId;
        int teamId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var writer = scope.ServiceProvider.GetRequiredService<MeetingRecord.Business.Services.Other.IRbacWriteService>();

            var preset = RolePresets.All.Single(x => x.Name == "檢視者");
            var role = new RoleView { Name = $"檢視者-{Guid.NewGuid():N}", TabViewJson = "[]" };
            var team = new Team { Name = $"檢視團隊-{Guid.NewGuid():N}", IsEnabled = true };
            db.RoleView.Add(role);
            db.Team.Add(team);
            await db.SaveChangesAsync();
            teamId = team.Id;
            await writer.SyncRolePermissionsAsync(role.Id, preset.PermissionKeys);

            var project = new Project { Title = $"檢視者看得到的專案-{Guid.NewGuid():N}", Status = "進行中", Owner = "x" };
            project.Teams.Add(new ProjectTeam { TeamId = teamId, IsPrimary = true });
            db.Project.Add(project);

            var user = new MyUser
            {
                Account = account,
                Name = "viewer",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            projectId = project.Id;
            await writer.SyncUserRolesAsync(user.Id, [role.Id]);
            db.UserTeam.Add(new UserTeam { MyUserId = user.Id, TeamId = teamId });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/Project/{projectId}")).StatusCode);

        var dto = new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = $"檢視者想建的專案-{Guid.NewGuid():N}",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            Owner = "viewer",
            PrimaryTeamId = teamId,
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/Project", dto)).StatusCode);

        dto.Id = projectId;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/Project/{projectId}", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/Project/{projectId}")).StatusCode);
    }

    [Fact]
    public async Task ProjectApi_OtherTeamsProject_ShouldBeHidden_AndCreatorTeamBecomesPrimary()
    {
        // 0.4.102：API 與畫面套同一條團隊規則（主責＋協作）。這筆同時守住「JWT 放的是 NameIdentifier 而不是 Sid」——
        // 0.4.98 以前 API 解析不到使用者，所有呼叫都被當成「非管理員、無團隊」。
        var account = $"member-{Guid.NewGuid():N}";
        const string password = "member-pass";
        int othersProjectId;
        int myGroupId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var writer = scope.ServiceProvider.GetRequiredService<MeetingRecord.Business.Services.Other.IRbacWriteService>();

            var role = new RoleView { Name = $"專案-{Guid.NewGuid():N}", TabViewJson = "[]" };
            db.RoleView.Add(role);
            var otherGroup = new Team { Name = $"別的團隊-{Guid.NewGuid():N}", IsEnabled = true };
            var myGroup = new Team { Name = $"我的團隊-{Guid.NewGuid():N}", IsEnabled = true };
            db.Team.AddRange(otherGroup, myGroup);
            var others = new Project { Title = $"別人的專案-{Guid.NewGuid():N}", Status = "進行中", Owner = "someone" };
            others.Teams.Add(new ProjectTeam { Team = otherGroup, IsPrimary = true });
            db.Project.Add(others);
            await db.SaveChangesAsync();
            myGroupId = myGroup.Id;
            othersProjectId = others.Id;
            await writer.SyncRolePermissionsAsync(role.Id, [MeetingRecord.Share.Helpers.MagicObjectHelper.角色_專案項目]);

            var user = new MyUser
            {
                Account = account,
                Name = "member",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, [role.Id]);
            db.UserTeam.Add(new UserTeam { MyUserId = user.Id, TeamId = myGroupId });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        var hidden = await client.GetAsync($"/api/Project/{othersProjectId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        var createResponse = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = $"自己的專案-{Guid.NewGuid():N}",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            Owner = "表單隨便填",
        });
        var created = await ReadApiResultAsync<ProjectDto>(createResponse);
        Assert.True(created.Success);

        var mine = await client.GetAsync($"/api/Project/{created.Data!.Id}");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        // 負責人回到手打的描述欄位；沒指定主責時用建立者的團隊當主責，所以他自己看得到。
        Assert.Equal("表單隨便填", (await ReadApiResultAsync<ProjectDto>(mine)).Data!.Owner);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var groups = db.ProjectTeam.Where(x => x.ProjectId == created.Data.Id).Select(x => x.TeamId).ToList();
            Assert.Equal([myGroupId], groups);
        }
    }

    [Fact]
    public async Task AuthEndpoints_LoginRefreshAndMe_ShouldReturnApiResult()
    {
        using var client = factory.CreateClient();

        var loginResult = await LoginAsync(client);
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.RefreshToken));

        var refreshResponse = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto
        {
            RefreshToken = loginResult.Data!.RefreshToken
        });
        var refreshResult = await ReadApiResultAsync<TokenResponseDto>(refreshResponse);

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        Assert.True(refreshResult.Success);
        Assert.False(string.IsNullOrWhiteSpace(refreshResult.Data?.AccessToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data.AccessToken);
        var meResponse = await client.GetAsync("/api/Auth/me");
        var meResult = await ReadApiResultAsync<CurrentUserDto>(meResponse);

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.True(meResult.Success);
        Assert.Equal("support", meResult.Data?.Account);
    }

    [Fact]
    public async Task VersionedAuthEndpoints_ShouldKeepApiResultContract()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });
        var result = await ReadApiResultAsync<TokenResponseDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Data?.AccessToken));
    }

    [Fact]
    public async Task ProjectCreate_InvalidPayload_ShouldReturnApiResult400()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.PostAsJsonAsync("/api/Project", new { });
        var result = await ReadApiResultAsync<ProjectDto>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(result.Errors);
    }

    /// <summary>建一個團隊給建立專案的測試當主責團隊。</summary>
    private async Task<int> SeedTeamAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var team = new Team { Name = $"團隊-{Guid.NewGuid():N}", IsEnabled = true };
        db.Team.Add(team);
        await db.SaveChangesAsync();
        return team.Id;
    }

    [Fact]
    public async Task ProjectCrud_WithBearerToken_ShouldUseApiResultAndDto()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        // Id 用 0 讓 EF 自動配號：controller 會把 DTO 的 Id 直接 map 進實體再 Add，
        // 寫死 1 的話只要資料庫裡已經有第 1 筆就撞主鍵，測試順序一變就紅。
        var createDto = new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = $"Integration Project {Guid.NewGuid():N}",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            CompletionPercentage = 10,
            Owner = "integration-test",
            // 主責團隊必填（0.4.102）；測試用的管理者帳號不屬於任何團隊，所以明確指定。
            PrimaryTeamId = await SeedTeamAsync(),
        };

        var createResponse = await client.PostAsJsonAsync("/api/Project", createDto);
        var createResult = await ReadApiResultAsync<ProjectDto>(createResponse);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.True(createResult.Success);
        Assert.NotNull(createResult.Data);
        Assert.True(createResult.Data!.Id > 0);
        Assert.Equal(createDto.Title, createResult.Data.Title);

        var getResponse = await client.GetAsync($"/api/Project/{createResult.Data.Id}");
        var getResult = await ReadApiResultAsync<ProjectDto>(getResponse);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResult.Success);
        Assert.Equal(createDto.Title, getResult.Data?.Title);
    }

    [Fact]
    public async Task ProjectUpdate_WithoutGlossaryFields_ShouldNotWipeExistingLists()
    {
        // ⚠️ 迴歸測試。ProjectRepository.UpdateAsync 用 CurrentValues.SetValues 覆寫所有純量欄位，
        // 而 controller 傳進去的是 mapper.Map<Project>(dto) 產生的全新實體——不特別保留的話，
        // 一次不含名單的 PUT 就會把使用者建好的名詞表清光，而且完全不會報錯。
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var title = $"Glossary Project {Guid.NewGuid():N}";
        // ⚠️ Id 用 0 不是 1：controller 會把 DTO 的 Id 直接 map 進實體再 Add，
        // 寫死 1 的話與其他建立專案的測試撞主鍵（同一個 in-memory DB 是共用的）。
        var createResponse = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = title,
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            CompletionPercentage = 10,
            Owner = "integration-test",
            PrimaryTeamId = await SeedTeamAsync(),
            GlossaryTerms = ["甲專案", "乙系統"],
            Participants = ["王小明"],
        });

        var created = await ReadApiResultAsync<ProjectDto>(createResponse);
        Assert.True(created.Success);
        var projectId = created.Data!.Id!.Value;

        // 舊版客戶端：完全不知道這兩個欄位的存在。
        var updateResponse = await client.PutAsJsonAsync($"/api/Project/{projectId}", new ProjectCreateUpdateDto
        {
            Id = projectId,
            Title = title,
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(14),
            Status = "已完成",
            CompletionPercentage = 100,
            Owner = "integration-test",
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var getResult = await ReadApiResultAsync<ProjectDto>(
            await client.GetAsync($"/api/Project/{projectId}"));

        Assert.Equal(["甲專案", "乙系統"], getResult.Data!.GlossaryTerms);
        Assert.Equal(["王小明"], getResult.Data.Participants);
        // 有帶的欄位仍然照常更新。
        Assert.Equal("已完成", getResult.Data.Status);
    }

    [Fact]
    public async Task ForbiddenApi_ShouldReturnApiResult403()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/forbidden");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task UnhandledApiException_ShouldReturnApiResult500()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/throw");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
        Assert.NotNull(result.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.Exception.Type);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task HealthReadiness_ShouldReturnHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthLiveness_ShouldReturnHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SystemHealthPage_WithoutCookieLogin_ShouldNotExposeDetails()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/system-health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK
            || response.StatusCode == HttpStatusCode.Redirect
            || response.StatusCode == HttpStatusCode.Unauthorized);
        Assert.DoesNotContain("最後 100 筆日誌紀錄", body);
    }

    [Fact]
    public void ProductionSafetyValidation_WithDevelopmentDefaults_ShouldFailFast()
    {
        var settings = new Dictionary<string, string?>
        {
            ["JwtSettings:SigningKey"] = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars",
            ["BootstrapSettings:SupportAccount"] = "support",
            ["BootstrapSettings:SupportPassword"] = "support",
            ["Swagger:EnabledInProduction"] = ""
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("JwtSettings:SigningKey", exception.Message);
        Assert.Contains("BootstrapSettings:SupportPassword", exception.Message);
        Assert.Contains("Swagger:EnabledInProduction", exception.Message);
    }

    private static async Task AuthorizeAsync(HttpClient client)
    {
        var loginResult = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);
    }

    private static async Task<ApiResult<TokenResponseDto>> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });

        var result = await ReadApiResultAsync<TokenResponseDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        return result;
    }

    private static async Task<ApiResult<T>> ReadApiResultAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult<T>>(json, JsonOptions);
        Assert.NotNull(result);
        return result!;
    }
}

public sealed class ApiTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "MeetingRecordIntegrationTests",
        Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string> environmentVariables;

    public ApiTestApplicationFactory()
    {
        environmentVariables = CreateSettings()
            .ToDictionary(
                x => x.Key.Replace(":", "__", StringComparison.Ordinal),
                x => x.Value ?? string.Empty);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(CreateSettings());
        });
        builder.ConfigureServices(services =>
        {
            services.AddAuthorization(options =>
            {
                options.AddPolicy("IntegrationForbidden", policy =>
                    policy.RequireClaim("integration_forbidden", "true"));
            });

            services
                .AddControllers()
                .PartManager
                .ApplicationParts
                .Add(new AssemblyPart(typeof(ContractProbeController).Assembly));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, null);
        }

        if (disposing && Directory.Exists(rootPath))
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may release file handles shortly after the test host stops.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup keeps integration test assertions independent from OS file timing.
            }
        }
    }

    private Dictionary<string, string?> CreateSettings()
    {
        return new Dictionary<string, string?>
        {
            ["NLog:BasePath"] = Path.Combine(rootPath, "Logs"),
            ["Security:ReturnExceptionDetails"] = "true",
            ["JwtSettings:Issuer"] = "MeetingRecord.Tests",
            ["JwtSettings:Audience"] = "MeetingRecord.Tests.Api",
            ["JwtSettings:SigningKey"] = "IntegrationTests-ChangeThisJwtSigningKey-AtLeast32Chars",
                ["JwtSettings:AccessTokenMinutes"] = "30",
                ["JwtSettings:RefreshTokenDays"] = "7",
                ["JwtSettings:ClockSkewMinutes"] = "0",
                ["BootstrapSettings:SupportAccount"] = "support",
                ["BootstrapSettings:SupportName"] = "support",
                ["BootstrapSettings:SupportEmail"] = "support",
                ["BootstrapSettings:SupportPassword"] = "support",
                ["SystemSettings:ExternalFileSystem:DatabasePath"] = Path.Combine(rootPath, "DB"),
            ["SystemSettings:ExternalFileSystem:DownloadPath"] = Path.Combine(rootPath, "Download"),
            ["SystemSettings:ExternalFileSystem:UploadPath"] = Path.Combine(rootPath, "Upload"),
            ["SystemSettings:ExternalFileSystem:ProjectFilePath"] = Path.Combine(rootPath, "ProjectFile"),
            // 0.4.115：以前沒設，沿用 appsettings.json 的 C:\temp\MeetingRecord\…——API 刪除專案或會議時，
            // 會用測試資料庫的編號去刪本機開發環境同編號的 AI 對話與實體檔。
            ["SystemSettings:ExternalFileSystem:MeetingMediaPath"] = Path.Combine(rootPath, "MeetingMedia"),
            ["SystemSettings:ExternalFileSystem:MeetingTranscriptPath"] = Path.Combine(rootPath, "MeetingTranscript"),
            ["SystemSettings:ExternalFileSystem:AiChatPath"] = Path.Combine(rootPath, "AiChat")
        };
    }
}

[ApiController]
[Route("api/[controller]")]
public sealed class ContractProbeController : ControllerBase
{
    [HttpGet("forbidden")]
    [Authorize(
        AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
        Policy = "IntegrationForbidden")]
    public IActionResult ForbiddenProbe()
    {
        return Ok();
    }

    [HttpGet("throw")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult ThrowProbe()
    {
        throw new InvalidOperationException("Integration probe exception.");
    }
}
