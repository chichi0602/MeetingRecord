namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 目前使用者對紀錄的存取範圍：是否管理員、以及授權團隊清單。
/// </summary>
public sealed record RecordAccessScope(bool IsAdmin, IReadOnlyList<string> Teams);

/// <summary>
/// 解析目前使用者的紀錄存取範圍。需同時支援 Blazor（CurrentUserService）
/// 與 Web API／檔案下載（HttpContext claims）兩種情境。
/// </summary>
public interface IRecordAccessScopeProvider
{
    Task<RecordAccessScope> GetAsync();
}
