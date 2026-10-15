using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>Google 登入時的帳號連結與自動建立都要留稽核（1.0.33）。</summary>
public sealed class ExternalLoginServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");

    public ExternalLoginServiceTests()
    {
        connection.Open();
        using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    [Fact]
    public async Task FindOrCreate_NewGoogleUser_ShouldWriteGoogleCreate()
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();

        var user = await CreateService(context).FindOrCreateAsync("Google", "sub-1", "new@example.com", "New Person", "一般使用者");

        Assert.False(user.Status);
        var audit = await context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal(AuditActions.UserGoogleCreate, audit.Action);
        Assert.Equal(user.Id, audit.ActorUserId);
        Assert.Equal("MyUser", audit.TargetType);
        Assert.Equal(user.Id.ToString(), audit.TargetId);
        Assert.Contains("待審核", audit.Detail);
    }

    [Fact]
    public async Task FindOrCreate_ExistingEmail_ShouldWriteGoogleLink()
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        var existing = new MyUser { Account = "alice", Name = "Alice", Email = "Alice@Example.com", Password = string.Empty, Status = true };
        context.MyUser.Add(existing);
        await context.SaveChangesAsync();

        var user = await CreateService(context).FindOrCreateAsync("Google", "sub-2", "alice@example.com", "Alice", "一般使用者");

        Assert.Equal(existing.Id, user.Id);
        var audit = await context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal(AuditActions.UserGoogleLink, audit.Action);
        Assert.Equal("alice", audit.ActorAccount);
    }

    [Fact]
    public async Task FindOrCreate_AlreadyLinked_ShouldNotWriteAudit()
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.MyUser.Add(new MyUser { Account = "bob", Name = "Bob", Password = string.Empty, Status = true, GoogleId = "sub-3" });
        await context.SaveChangesAsync();

        await CreateService(context).FindOrCreateAsync("Google", "sub-3", "bob@example.com", "Bob", "一般使用者");

        Assert.False(await context.AuditLog.AnyAsync());
    }

    private static ExternalLoginService CreateService(BackendDBContext context)
        => new(context, NullLogger<ExternalLoginService>.Instance, TestAuditLogs.CreateService(context));
}
