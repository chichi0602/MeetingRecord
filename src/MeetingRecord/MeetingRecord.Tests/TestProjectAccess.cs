using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

/// <summary>
/// 測試用的專案存取範圍（0.4.99）。服務層的資料權限一律經 <see cref="ProjectAccessService"/>，
/// 這裡用固定的使用者身分建一個出來；既有測試預設用管理者，行為與改版前一致。
/// </summary>
internal static class TestProjectAccess
{
    public static ProjectAccessService Admin(BackendDBContext context, int userId = 0)
        => new(context, new FixedScopeProvider(new RecordAccessScope(true, [], userId)));

    public static ProjectAccessService User(BackendDBContext context, int userId)
        => new(context, new FixedScopeProvider(new RecordAccessScope(false, [], userId)));

    private sealed class FixedScopeProvider(RecordAccessScope scope) : IRecordAccessScopeProvider
    {
        public Task<RecordAccessScope> GetAsync() => Task.FromResult(scope);
    }
}
