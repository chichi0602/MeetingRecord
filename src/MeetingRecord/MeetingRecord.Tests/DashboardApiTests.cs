using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Dashboards;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 儀表板 API（<c>api/v1/dashboard</c>，1.1.12）。登入即可看；看不到的專案名稱併成「其他專案」，
/// 而且是用 API 呼叫者自己的資料範圍判斷。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class DashboardApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "dashboard-api-pass";
    private const int MeetingsPerSeededProject = 30; // 夠多，才進得了「前 8 名」
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Anonymous_ShouldBeUnauthorized()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/v1/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProjectNames_ShouldFollowTheCallersOwnScope()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        string mine = $"我的專案-{suffix}", others = $"別人的專案-{suffix}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var myTeam = new Team { Name = $"我的團隊-{suffix}", IsEnabled = true };
            var otherTeam = new Team { Name = $"別的團隊-{suffix}", IsEnabled = true };
            db.UserTeam.Add(new UserTeam { MyUserId = userId, Team = myTeam });
            AddProjectWithMeetings(db, mine, myTeam);
            AddProjectWithMeetings(db, others, otherTeam);
            await db.SaveChangesAsync();
        }

        // 一般使用者：自己的專案看得到名字，別人的併成「其他專案」。
        using (var client = await LoginAsync(account))
        {
            var summary = await GetSummaryAsync(client);
            Assert.NotEmpty(summary.Cards);
            Assert.Contains(summary.MeetingsPerProject, x => x.Label == mine);
            Assert.DoesNotContain(summary.MeetingsPerProject, x => x.Label == others);
            Assert.Contains(summary.MeetingsPerProject, x => x.Label == DashboardService.HiddenProjectLabel);
        }

        // 管理者：全部看得到。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            (await db.MyUser.SingleAsync(x => x.Id == userId)).IsAdmin = true;
            await db.SaveChangesAsync();
        }

        using (var admin = await LoginAsync(account))
        {
            var summary = await GetSummaryAsync(admin);
            Assert.Contains(summary.MeetingsPerProject, x => x.Label == mine);
            Assert.Contains(summary.MeetingsPerProject, x => x.Label == others);
        }
    }

    private static void AddProjectWithMeetings(BackendDBContext db, string title, Team team)
    {
        var project = new Project { Title = title, Status = "進行中", Owner = "someone" };
        project.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
        for (var i = 0; i < MeetingsPerSeededProject; i++)
        {
            db.Meeting.Add(new Meeting { Title = $"{title}-會議{i}", Project = project });
        }
    }

    private static async Task<DashboardSummary> GetSummaryAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<ApiResult<DashboardSummary>>(await response.Content.ReadAsStringAsync(), JsonOptions)!;
        return result.Data!;
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }
}
