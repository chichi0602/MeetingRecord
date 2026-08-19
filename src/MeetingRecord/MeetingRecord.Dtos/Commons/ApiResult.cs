namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// API 統一回應格式
/// </summary>
/// <typeparam name="T">回應資料的型別</typeparam>
public class ApiResult<T>
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// HTTP 狀態碼
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// 訊息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 回應資料
    /// </summary>
    public T? Data { get; set; }

    /// <summary>
    /// 欄位或規則驗證錯誤。
    /// </summary>
    public Dictionary<string, string[]>? Errors { get; set; }

    /// <summary>
    /// 本次請求追蹤識別碼。
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// 例外資訊。此腳手架依需求完整回傳，正式環境請評估資訊揭露風險。
    /// </summary>
    public ApiExceptionInfo? Exception { get; set; }

    /// <summary>
    /// 錯誤訊息 (當失敗時)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 錯誤詳細資訊 (開發環境用)
    /// </summary>
    public string? ErrorDetail { get; set; }

    /// <summary>
    /// 時間戳記
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public static ApiResult<T> FromException(Exception exception, string message = "伺服器發生錯誤", int statusCode = 500, string? traceId = null)
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = statusCode,
            Message = "伺服器錯誤",
            ErrorMessage = message,
            ErrorDetail = exception.ToString(),
            TraceId = traceId,
            Exception = ApiExceptionInfo.FromException(exception)
        };
    }

    #region 靜態工廠方法

    /// <summary>
    /// 建立成功的回應
    /// </summary>
    /// <param name="data">回應資料</param>
    /// <param name="message">訊息</param>
    /// <returns></returns>
    public static ApiResult<T> SuccessResult(T data, string message = "操作成功")
    {
        return new ApiResult<T>
        {
            Success = true,
            StatusCode = 200,
            Message = message,
            Data = data
        };
    }

    /// <summary>
    /// 建立失敗的回應
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <param name="statusCode">HTTP 狀態碼</param>
    /// <param name="errorDetail">錯誤詳細資訊</param>
    /// <returns></returns>
    public static ApiResult<T> FailureResult(string message, int statusCode = 400, string? errorDetail = null)
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = statusCode,
            Message = "操作失敗",
            ErrorMessage = message,
            ErrorDetail = errorDetail,
            Errors = new Dictionary<string, string[]>
            {
                ["General"] = new[] { message }
            }
        };
    }

    /// <summary>
    /// 建立驗證失敗的回應
    /// </summary>
    /// <param name="message">驗證錯誤訊息</param>
    /// <returns></returns>
    public static ApiResult<T> ValidationError(string message)
    {
        return ValidationError(new Dictionary<string, string[]>
        {
            ["Validation"] = new[] { message }
        });
    }

    /// <summary>
    /// 建立驗證失敗的回應
    /// </summary>
    /// <param name="errors">欄位驗證錯誤</param>
    /// <returns></returns>
    public static ApiResult<T> ValidationError(Dictionary<string, string[]> errors)
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = 400,
            Message = "驗證失敗",
            ErrorMessage = string.Join("; ", errors.SelectMany(x => x.Value)),
            Errors = errors
        };
    }

    /// <summary>
    /// 建立找不到資源的回應
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <returns></returns>
    public static ApiResult<T> NotFoundResult(string message = "找不到指定的資源")
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = 404,
            Message = "資源不存在",
            ErrorMessage = message,
            Errors = new Dictionary<string, string[]>
            {
                ["NotFound"] = new[] { message }
            }
        };
    }

    /// <summary>
    /// 建立未授權的回應
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <returns></returns>
    public static ApiResult<T> UnauthorizedResult(string message = "未授權存取")
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = 401,
            Message = "未授權",
            ErrorMessage = message,
            Errors = new Dictionary<string, string[]>
            {
                ["Unauthorized"] = new[] { message }
            }
        };
    }

    /// <summary>
    /// 建立禁止存取的回應
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <returns></returns>
    public static ApiResult<T> ForbiddenResult(string message = "無權限存取此資源")
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = 403,
            Message = "禁止存取",
            ErrorMessage = message,
            Errors = new Dictionary<string, string[]>
            {
                ["Forbidden"] = new[] { message }
            }
        };
    }

    /// <summary>
    /// 建立衝突的回應 (例如:資料重複)
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <returns></returns>
    public static ApiResult<T> ConflictResult(string message)
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = 409,
            Message = "資料衝突",
            ErrorMessage = message,
            Errors = new Dictionary<string, string[]>
            {
                ["Conflict"] = new[] { message }
            }
        };
    }

    /// <summary>
    /// 建立伺服器錯誤的回應
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <param name="errorDetail">錯誤詳細資訊</param>
    /// <returns></returns>
    public static ApiResult<T> ServerErrorResult(string message = "伺服器發生錯誤", string? errorDetail = null)
    {
        return new ApiResult<T>
        {
            Success = false,
            StatusCode = 500,
            Message = "伺服器錯誤",
            ErrorMessage = message,
            ErrorDetail = errorDetail
        };
    }

    /// <summary>
    /// 建立伺服器錯誤的回應
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <param name="exception">例外資訊</param>
    /// <returns></returns>
    public static ApiResult<T> ServerErrorResult(string message, Exception exception)
    {
        return FromException(exception, message);
    }

    #endregion
}

public class ApiExceptionInfo
{
    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? StackTrace { get; set; }

    public string? Source { get; set; }

    public ApiExceptionInfo? InnerException { get; set; }

    public static ApiExceptionInfo FromException(Exception exception)
    {
        return new ApiExceptionInfo
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
            Source = exception.Source,
            InnerException = exception.InnerException == null
                ? null
                : FromException(exception.InnerException)
        };
    }
}

/// <summary>
/// 非泛型版本的 ApiResult (不需要資料時使用)
/// </summary>
public class ApiResult : ApiResult<object>
{
    #region 靜態工廠方法

    /// <summary>
    /// 建立成功的回應 (無資料)
    /// </summary>
    /// <param name="message">訊息</param>
    /// <returns></returns>
    public static ApiResult SuccessResult(string message = "操作成功")
    {
        return new ApiResult
        {
            Success = true,
            StatusCode = 200,
            Message = message
        };
    }

    /// <summary>
    /// 建立失敗的回應 (無資料)
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <param name="statusCode">HTTP 狀態碼</param>
    /// <param name="errorDetail">錯誤詳細資訊</param>
    /// <returns></returns>
    public static new ApiResult FailureResult(string message, int statusCode = 400, string? errorDetail = null)
    {
        return new ApiResult
        {
            Success = false,
            StatusCode = statusCode,
            Message = "操作失敗",
            ErrorMessage = message,
            ErrorDetail = errorDetail
        };
    }

    /// <summary>
    /// 建立驗證失敗的回應 (無資料)
    /// </summary>
    /// <param name="message">驗證錯誤訊息</param>
    /// <returns></returns>
    public static new ApiResult ValidationError(string message)
    {
        return ValidationError(new Dictionary<string, string[]>
        {
            ["Validation"] = new[] { message }
        });
    }

    /// <summary>
    /// 建立驗證失敗的回應 (無資料)
    /// </summary>
    /// <param name="errors">欄位驗證錯誤</param>
    /// <returns></returns>
    public static new ApiResult ValidationError(Dictionary<string, string[]> errors)
    {
        return new ApiResult
        {
            Success = false,
            StatusCode = 400,
            Message = "驗證失敗",
            ErrorMessage = string.Join("; ", errors.SelectMany(x => x.Value)),
            Errors = errors
        };
    }

    /// <summary>
    /// 建立找不到資源的回應 (無資料)
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <returns></returns>
    public static new ApiResult NotFoundResult(string message = "找不到指定的資源")
    {
        return new ApiResult
        {
            Success = false,
            StatusCode = 404,
            Message = "資源不存在",
            ErrorMessage = message
        };
    }

    /// <summary>
    /// 建立伺服器錯誤的回應 (無資料)
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <param name="errorDetail">錯誤詳細資訊</param>
    /// <returns></returns>
    public static new ApiResult ServerErrorResult(string message = "伺服器發生錯誤", string? errorDetail = null)
    {
        return new ApiResult
        {
            Success = false,
            StatusCode = 500,
            Message = "伺服器錯誤",
            ErrorMessage = message,
            ErrorDetail = errorDetail
        };
    }

    /// <summary>
    /// 建立伺服器錯誤的回應 (無資料)
    /// </summary>
    /// <param name="message">錯誤訊息</param>
    /// <param name="exception">例外資訊</param>
    /// <returns></returns>
    public static new ApiResult ServerErrorResult(string message, Exception exception)
    {
        return new ApiResult
        {
            Success = false,
            StatusCode = 500,
            Message = "伺服器錯誤",
            ErrorMessage = message,
            ErrorDetail = exception.ToString(),
            Exception = ApiExceptionInfo.FromException(exception)
        };
    }

    #endregion
}
