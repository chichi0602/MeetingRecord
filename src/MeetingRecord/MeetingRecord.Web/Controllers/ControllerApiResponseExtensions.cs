using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Web.Controllers;

public static class ControllerApiResponseExtensions
{
    public static ObjectResult ApiServerError<T>(
        this ControllerBase controller,
        string message,
        Exception exception)
    {
        var traceId = controller.HttpContext.TraceIdentifier;
        return controller.StatusCode(
            StatusCodes.Status500InternalServerError,
            ShouldReturnExceptionDetails(controller)
                ? ApiResult<T>.ServerErrorResult(message, exception)
                : new ApiResult<T>
                {
                    Success = false,
                    StatusCode = StatusCodes.Status500InternalServerError,
                    Message = "伺服器錯誤",
                    ErrorMessage = message,
                    TraceId = traceId
                });
    }

    public static ObjectResult ApiServerError(
        this ControllerBase controller,
        string message,
        Exception exception)
    {
        var traceId = controller.HttpContext.TraceIdentifier;
        return controller.StatusCode(
            StatusCodes.Status500InternalServerError,
            ShouldReturnExceptionDetails(controller)
                ? ApiResult.ServerErrorResult(message, exception)
                : new ApiResult
                {
                    Success = false,
                    StatusCode = StatusCodes.Status500InternalServerError,
                    Message = "伺服器錯誤",
                    ErrorMessage = message,
                    TraceId = traceId
                });
    }

    /// <summary>
    /// 與 <c>ApiExceptionFilterAttribute</c> 同一條規則（0.4.115）：未設定時只有開發環境回傳例外細節。
    /// 控制器都自己接住例外，全域過濾器根本跑不到；以前這裡一律回傳完整堆疊，正式環境會洩漏
    /// 資料庫結構、檔案路徑與內部訊息。
    /// </summary>
    private static bool ShouldReturnExceptionDetails(ControllerBase controller)
    {
        var services = controller.HttpContext.RequestServices;
        var settings = services.GetService<IOptions<SecuritySettings>>()?.Value;
        return settings?.ReturnExceptionDetails
            ?? services.GetService<IWebHostEnvironment>()?.IsDevelopment()
            ?? false;
    }
}
