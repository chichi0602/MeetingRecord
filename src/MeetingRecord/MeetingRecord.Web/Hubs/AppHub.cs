using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Hubs;

/// <summary>
/// WASM 前端的即時推播通道（1.1.2）：<c>/hubs/app</c>。頁面在瀏覽器執行，收不到伺服器程序內的事件，
/// 由這裡推給它。
///
/// <para>
/// 連線時加入自己的群組 <see cref="UserGroup"/>，伺服器只對群組送。推播**不帶資料**、只說「什麼變了」，
/// 前端收到再自己打 API 查——權限與資料範圍照樣由 API 把關，推播不會變成繞過權限的管道。
/// </para>
///
/// <para>
/// ⚠️ 目前沒有任何「用戶端呼叫伺服器」的方法。之後要加會改資料的方法時，要和 API 一樣檢查權限（<c>IPermissionChecker</c>）與資料範圍。
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class AppHub : Hub
{
    public const string Path = "/hubs/app";

    /// <summary>某些通知有變動（新通知或已讀），前端重查未讀數。</summary>
    public const string NotificationsChanged = "NotificationsChanged";

    /// <summary>
    /// 背景工作（上傳、轉錄、會議紀錄生成）的進度有變動（1.1.15），前端重查 <c>api/v1/jobs</c>。
    /// 送給所有連線：不帶任何資料，看得到哪些工作由 API 依呼叫者的資料範圍決定。
    /// </summary>
    public const string JobsChanged = "JobsChanged";

    public static string UserGroup(int userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        if (UserClaims.GetUserId(Context.User) is not { } userId)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        await base.OnConnectedAsync();
    }
}
