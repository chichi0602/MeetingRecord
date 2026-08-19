using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Controllers;

public static class ControllerApiResponseExtensions
{
    public static ObjectResult ApiServerError<T>(
        this ControllerBase controller,
        string message,
        Exception exception)
    {
        return controller.StatusCode(
            StatusCodes.Status500InternalServerError,
            ApiResult<T>.ServerErrorResult(message, exception));
    }

    public static ObjectResult ApiServerError(
        this ControllerBase controller,
        string message,
        Exception exception)
    {
        return controller.StatusCode(
            StatusCodes.Status500InternalServerError,
            ApiResult.ServerErrorResult(message, exception));
    }
}
