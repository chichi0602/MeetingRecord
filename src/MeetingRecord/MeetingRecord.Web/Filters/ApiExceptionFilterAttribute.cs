using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Web.Filters;

public class ApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    private readonly ILogger<ApiExceptionFilterAttribute> logger;
    private readonly SecuritySettings securitySettings;
    private readonly IWebHostEnvironment environment;

    public ApiExceptionFilterAttribute(
        ILogger<ApiExceptionFilterAttribute> logger,
        IOptions<SecuritySettings> securityOptions,
        IWebHostEnvironment environment)
    {
        this.logger = logger;
        securitySettings = securityOptions.Value;
        this.environment = environment;
    }

    public override void OnException(ExceptionContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            return;
        }

        logger.LogError(
            context.Exception,
            "Unhandled API exception. TraceId={TraceId}, Path={Path}",
            context.HttpContext.TraceIdentifier,
            context.HttpContext.Request.Path.Value);

        var result = ShouldReturnExceptionDetails()
            ? ApiResult.FromException(
                context.Exception,
                "API 呼叫過程發生例外。",
                StatusCodes.Status500InternalServerError,
                context.HttpContext.TraceIdentifier)
            : new ApiResult
            {
                Success = false,
                StatusCode = StatusCodes.Status500InternalServerError,
                Message = "伺服器錯誤",
                ErrorMessage = "API 呼叫過程發生例外。",
                TraceId = context.HttpContext.TraceIdentifier
            };

        context.Result = new ObjectResult(result)
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
        context.ExceptionHandled = true;
    }

    private bool ShouldReturnExceptionDetails()
    {
        return securitySettings.ReturnExceptionDetails ?? environment.IsDevelopment();
    }
}
