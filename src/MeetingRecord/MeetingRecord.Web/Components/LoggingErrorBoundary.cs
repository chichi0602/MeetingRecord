using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.Components;

/// <summary>
/// 會寫日誌的 <see cref="ErrorBoundary"/>。
///
/// 內建的 ErrorBoundary 只會把元件切換到錯誤畫面，**不會記錄任何東西** ——
/// 必須覆寫 OnErrorAsync 才留得下痕跡。在此之前，Blazor 元件內的未處理例外
/// 在日誌上完全看不到。
///
/// 這是最後一道網：個別寫入操作已各自 try/catch，這裡負責接住其餘所有位置
/// （渲染、生命週期、未包裝的事件處理）拋出的例外。
/// </summary>
public sealed class LoggingErrorBoundary : ErrorBoundary
{
    [Inject]
    private ILogger<LoggingErrorBoundary> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    protected override Task OnErrorAsync(Exception exception)
    {
        var user = CurrentUserService.CurrentUser;
        var account = string.IsNullOrWhiteSpace(user.Account) ? "(anonymous)" : user.Account;

        Logger.LogError(
            exception,
            "Unhandled component exception captured by error boundary. Path={Path}, Account={Account}, UserId={UserId}",
            SafeRelativePath(),
            account,
            user.Id);

        // 讓 ErrorContent 顯示得出這次互動的錯誤追蹤碼：它拿得到例外，拿不到當時的追蹤範圍。
        TraceCode.Attach(exception);

        return base.OnErrorAsync(exception);
    }

    private string SafeRelativePath()
    {
        try
        {
            var relative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
            var queryIndex = relative.IndexOf('?');
            if (queryIndex >= 0)
            {
                relative = relative[..queryIndex];
            }

            return string.IsNullOrEmpty(relative) ? "/" : "/" + relative;
        }
        catch (ArgumentException)
        {
            return "(unknown)";
        }
    }
}
