using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Filters;

public class ApiValidationFilterAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.Controller is not ControllerBase controllerBase)
        {
            base.OnActionExecuting(context);
            return;
        }

        var modelState = controllerBase.ModelState;
        if (modelState.IsValid)
        {
            base.OnActionExecuting(context);
            return;
        }

        var errors = modelState
            .Where(x => x.Value?.Errors.Count > 0)
            .ToDictionary(
                x => x.Key,
                x => x.Value!.Errors
                    .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage)
                        ? "欄位驗證失敗。"
                        : e.ErrorMessage)
                    .ToArray());

        // 根據回傳型別決定 ApiResult 泛型
        // 這裡假設 Create 回傳 ApiResult<ProjectDto>、Update 回傳 ApiResult
        var returnType = (context.ActionDescriptor as Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)?
            .MethodInfo.ReturnType;

        if (returnType?.IsGenericType == true &&
            returnType.GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            var innerType = returnType.GetGenericArguments()[0];
            if (innerType.IsGenericType &&
                innerType.GetGenericTypeDefinition() == typeof(ApiResult<>))
            {
                var apiResultGenericType = innerType.GetGenericArguments()[0];
                var genericMethod = typeof(ApiValidationFilterAttribute)
                    .GetMethod(nameof(CreateGenericValidationResult), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .MakeGenericMethod(apiResultGenericType);

                var result = (IActionResult)genericMethod.Invoke(null, new object[] { errors })!;
                context.Result = result;
                return;
            }
        }

        // 非泛型 ApiResult 或推斷失敗時走這裡
        context.Result = new BadRequestObjectResult(ApiResult.ValidationError(errors));
    }

    private static IActionResult CreateGenericValidationResult<T>(Dictionary<string, string[]> errors)
    {
        return new BadRequestObjectResult(ApiResult<T>.ValidationError(errors));
    }
}
