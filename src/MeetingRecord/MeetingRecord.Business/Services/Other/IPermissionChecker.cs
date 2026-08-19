namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 判斷使用者的有效權限。作為 UI 與 API 共用的權限判定單一來源。
/// 目前以權威模型（使用者角色的 TabViewJson）計算；階段三後續改讀 RBAC 關聯表。
/// </summary>
public interface IPermissionChecker
{
    /// <summary>使用者是否具備指定權限鍵（管理員一律為 true）。</summary>
    Task<bool> HasPermissionAsync(int userId, string permissionKey);

    /// <summary>取得使用者的有效權限鍵集合（不含管理員全通過的隱含權限）。</summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId);
}
