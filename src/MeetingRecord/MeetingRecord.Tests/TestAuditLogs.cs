using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

/// <summary>
/// 測試用的 <see cref="AuditLogService"/>（1.0.32 起寫入服務改用自己新建的 context）：
/// 沿用測試 context 的同一組 options——也就是同一條 in-memory SQLite 連線，寫進去的稽核測試讀得到。
/// </summary>
internal static class TestAuditLogs
{
    public static AuditLogService CreateService(
        BackendDBContext context,
        IClientIpProvider? clientIpProvider = null,
        ILogger<AuditLogService>? logger = null)
        => new(OptionsOf(context), logger ?? NullLogger<AuditLogService>.Instance, clientIpProvider);

    public static DbContextOptions<BackendDBContext> OptionsOf(BackendDBContext context)
        => (DbContextOptions<BackendDBContext>)context.GetService<IDbContextOptions>();

    /// <summary>DI 版：背景服務測試自己組 ServiceCollection 時註冊這一份，<see cref="AuditLogService"/> 才解析得到。</summary>
    public static DbContextOptions<BackendDBContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
}
