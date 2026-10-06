using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Enums;
using MeetingRecord.Web.Configuration;
using MeetingRecord.Web.Controllers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetingRecord.Tests;

/// <summary>
/// 上線前審查（0.4.115）在 API 這一層找到的問題：帳號狀態、選項白名單、會議刪除與編號、錯誤細節。
/// </summary>
public sealed class GoLiveReviewApiTests : IClassFixture<ApiTestApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ApiTestApplicationFactory factory;

    public GoLiveReviewApiTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Refresh_AfterAccountDisabled_ShouldReturn401()
    {
        // 以前換發權杖不查資料庫：被停用的人在 7 天內都能一直換到新的存取權杖。
        var (account, password, userId) = await SeedUserAsync();
        using var client = factory.CreateClient();
        var login = await ReadAsync<TokenResponseDto>(
            await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password }));
        Assert.True(login.Success);

        await WithDbAsync(db => db.MyUser.Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, false)));

        var refresh = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = login.Data!.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Login_WhenPasswordChangeRequired_ShouldReturn401()
    {
        // 強制改密碼以前只在網頁生效，管理者發的初始密碼可以直接拿來用 API。
        var (account, password, userId) = await SeedUserAsync();
        await WithDbAsync(db => db.MyUser.Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.MustChangePassword, true)));
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task TodoApi_ShouldRejectUnknownStatusOrPriority_AndIgnoreClientId()
    {
        var projectId = await SeedProjectAsync();
        using var client = await CreateSupportClientAsync();

        var badStatus = await client.PostAsJsonAsync("/api/Todo", new TodoCreateUpdateDto
        {
            Id = 0, Title = "亂狀態", ProjectId = projectId, Priority = "中", Status = "做一半",
        });
        var badPriority = await client.PostAsJsonAsync("/api/Todo", new TodoCreateUpdateDto
        {
            Id = 0, Title = "亂優先度", ProjectId = projectId, Priority = "超急", Status = "待辦",
        });
        var forcedId = await client.PostAsJsonAsync("/api/Todo", new TodoCreateUpdateDto
        {
            Id = 876543, Title = "指定編號", ProjectId = projectId, Priority = "中", Status = "待辦",
        });

        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPriority.StatusCode);
        var created = await ReadAsync<TodoDto>(forcedId);
        Assert.True(created.Success);
        Assert.NotEqual(876543, created.Data!.Id);
    }

    [Fact]
    public async Task MeetingApi_Create_ShouldIgnoreClientId()
    {
        // 照抄用戶端的編號，可以探測別人的會議存不存在，也能接手已刪除會議的編號（AI 對話檔只用編號定位）。
        using var client = await CreateSupportClientAsync();

        var response = await client.PostAsJsonAsync("/api/Meeting", new MeetingCreateUpdateDto { Id = 765432, Title = "指定編號的會議" });
        var created = await ReadAsync<MeetingDto>(response);

        Assert.True(created.Success);
        Assert.NotEqual(765432, created.Data!.Id);
    }

    [Fact]
    public async Task MeetingApi_Delete_ShouldGoThroughServiceRules_AndRemoveAiChat()
    {
        int busyId = 0, idleId = 0;
        await WithDbAsync(async db =>
        {
            var busy = new Meeting { Title = "轉錄中", TranscriptionStatus = TranscriptionStatus.Processing };
            var idle = new Meeting { Title = "可以刪" };
            db.Meeting.AddRange(busy, idle);
            await db.SaveChangesAsync();
            busyId = busy.Id;
            idleId = idle.Id;
        });
        var chatDir = Path.Combine(GetSettings().ExternalFileSystem.AiChatPath, "meeting", idleId.ToString());
        Directory.CreateDirectory(chatDir);
        await File.WriteAllTextAsync(Path.Combine(chatDir, "c1.jsonl"), "{}");
        using var client = await CreateSupportClientAsync();

        var busyResponse = await client.DeleteAsync($"/api/Meeting/{busyId}");
        var idleResponse = await client.DeleteAsync($"/api/Meeting/{idleId}");

        Assert.Equal(HttpStatusCode.BadRequest, busyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, idleResponse.StatusCode);
        Assert.False(Directory.Exists(chatDir));
    }

    [Theory]
    [InlineData("Production", null, false)]
    [InlineData("Development", null, true)]
    [InlineData("Production", true, true)]
    public void ApiServerError_ShouldHideExceptionDetails_UnlessDevelopmentOrEnabled(string environment, bool? configured, bool expectDetails)
    {
        // 控制器都自己接住例外，全域過濾器跑不到；以前這裡一律回傳完整堆疊。
        var services = new ServiceCollection()
            .AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new FakeEnvironment(environment))
            .AddSingleton(Options.Create(new SecuritySettings { ReturnExceptionDetails = configured }))
            .BuildServiceProvider();
        var controller = new ProbeController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } }
        };

        var result = controller.ApiServerError("失敗了", new InvalidOperationException("UNIQUE constraint failed: Meeting.Id"));
        var body = Assert.IsType<ApiResult>(result.Value);

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Equal("失敗了", body.ErrorMessage);
        Assert.Equal(expectDetails, body.ErrorDetail is not null);
        Assert.Equal(expectDetails, body.Exception is not null);
    }

    #region 輔助

    private sealed class ProbeController : ControllerBase;

    private sealed class FakeEnvironment(string name) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private MeetingRecord.Models.Systems.SystemSettings GetSettings()
        => factory.Services.GetRequiredService<IOptions<MeetingRecord.Models.Systems.SystemSettings>>().Value;

    private async Task WithDbAsync(Func<BackendDBContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<BackendDBContext>());
    }

    private async Task<(string Account, string Password, int UserId)> SeedUserAsync()
    {
        var account = $"golive-{Guid.NewGuid():N}";
        const string password = "golive-pass";
        var userId = 0;
        await WithDbAsync(async db =>
        {
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Status = true,
                Password = SecurePasswordHasher.HashPassword(password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        });
        return (account, password, userId);
    }

    private async Task<int> SeedProjectAsync()
    {
        var projectId = 0;
        await WithDbAsync(async db =>
        {
            var project = new Project { Title = $"上線審查-{Guid.NewGuid():N}", Status = "進行中", Owner = "x" };
            project.Teams.Add(new ProjectTeam { Team = new Team { Name = $"團隊-{Guid.NewGuid():N}", IsEnabled = true }, IsPrimary = true });
            db.Project.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        });
        return projectId;
    }

    private async Task<HttpClient> CreateSupportClientAsync()
    {
        var client = factory.CreateClient();
        var login = await ReadAsync<TokenResponseDto>(
            await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = "support", Password = "support" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Data!.AccessToken);
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        var result = JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(result);
        return result!;
    }

    #endregion
}
