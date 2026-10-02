namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 目前使用者對紀錄的存取範圍：是否管理員、授權團隊清單，以及使用者 Id（查所屬團隊與未歸屬會議的上傳者用；解析不到時為 0）。
/// </summary>
public sealed record RecordAccessScope(bool IsAdmin, IReadOnlyList<string> Teams, int UserId = 0);

/// <summary>
/// 解析目前使用者的紀錄存取範圍。需同時支援 Blazor（CurrentUserService）
/// 與 Web API／檔案下載（HttpContext claims）兩種情境。
/// </summary>
public interface IRecordAccessScopeProvider
{
    Task<RecordAccessScope> GetAsync();
}
