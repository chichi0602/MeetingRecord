using MeetingRecord.Dtos.Accounts;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// WASM 頁擋下使用者時寫「頁面拒絕存取」稽核（1.1.6），對應 Server 頁的 <c>AuthenticationStateHelper.RecordPageAccessDeniedAsync</c>。
/// ⚠️ 每個會顯示「你沒有權限存取此頁面」的 WASM 頁都要呼叫 <see cref="RecordPageDeniedAsync"/>（<c>PageAccessDeniedAuditTests</c> 掃原始碼守門）。
/// 寫不進去不影響畫面：稽核失敗只是少一筆紀錄。
/// </summary>
public sealed class PageAccessApi(ApiClient api)
{
    public async Task RecordPageDeniedAsync(string route)
    {
        try
        {
            await api.PostAsync(
                "api/v1/audit/page-denied",
                new PageDeniedRequestDto { Route = route },
                ClientJsonContext.Default.PageDeniedRequestDto,
                ClientJsonContext.Default.EmptyResult);
        }
        catch (HttpRequestException)
        {
        }
    }
}
