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
                TabViewJson = JsonSerializer.Serialize(new[] { "首頁" }),
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
            Priority = "中",
            Owner = "viewer",
        });
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
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

    [Fact]
    public async Task ProjectCrud_WithBearerToken_ShouldUseApiResultAndDto()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var createDto = new ProjectCreateUpdateDto
        {
            Id = 1,
            Title = $"Integration Project {Guid.NewGuid():N}",
            Description = "Integration test project",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            Priority = "中",
            CompletionPercentage = 10,
            Owner = "integration-test"
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
            ["SystemSettings:ExternalFileSystem:ProjectFilePath"] = Path.Combine(rootPath, "ProjectFile")
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
