using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Configuration;
using MeetingRecord.Web.Diagnostics;
using System.Diagnostics;

namespace MeetingRecord.Web.Extensions;

public static class ApplicationBuilderExtensions
{
    public static WebApplication UseConfiguredSwagger(this WebApplication app, ILogger logger)
    {
        var swaggerSettings = app.Configuration
            .GetSection(SwaggerSettings.SectionName)
            .Get<SwaggerSettings>() ?? new SwaggerSettings();

        if (!app.Environment.IsDevelopment()
            && !(app.Environment.IsProduction() && swaggerSettings.EnabledInProduction))
        {
            return app;
        }

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "MeetingRecord API v1");
        });
        logger.LogInformation("Swagger UI enabled.");
        return app;
    }

    public static WebApplication UseConfiguredForwardedHeaders(this WebApplication app)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        return app;
    }

    public static WebApplication UseConfiguredCors(this WebApplication app)
    {
        app.UseCors("ConfiguredCors");
        return app;
    }

    /// <summary>TraceIdentifier 已被換成追蹤碼的標記鍵（0.4.122）。</summary>
    private const string TraceCodeItemKey = "MeetingRecord.TraceCode";

    /// <summary>
    /// 全域請求日誌。0.4.122 起另外負責：
    /// <list type="bullet">
    /// <item>每個請求一組錯誤追蹤碼（<see cref="TraceCode"/>），並<b>取代 <c>HttpContext.TraceIdentifier</c></b>——
    /// API 回應的 TraceId 與框架自己的日誌都是同一個碼，使用者回報的碼可在日誌檢視與系統例外紀錄查到。
    /// （/Error 頁仍顯示 Activity Id，不是追蹤碼。）</item>
    /// <item>系統例外紀錄的情境（來源 WebAPI／畫面、路徑）；這時還沒驗證，帳號由 <see cref="UseExceptionContextUser"/> 補。</item>
    /// </list>
    /// </summary>
    public static WebApplication UseHttpRequestLogging<TProgram>(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var requestLogger = context.RequestServices.GetRequiredService<ILogger<TProgram>>();
            var stopwatch = Stopwatch.StartNew();

            if (context.Items[TraceCodeItemKey] is not string traceCode)
            {
                traceCode = TraceCode.New();
                context.Items[TraceCodeItemKey] = traceCode;
                context.TraceIdentifier = traceCode;
            }

            using var traceScope = TraceCode.Begin(traceCode);

            try
            {
                var path = context.Request.Path.Value;
                var source = path is not null && path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                    ? ExceptionSources.WebApi
                    : ExceptionSources.Ui;
                var (account, userId) = RequestActorResolver.Resolve(context.User);

                context.RequestServices.GetRequiredService<ExceptionContextAccessor>()
                    .Set(new ExceptionContext(source, path, account, userId));
            }
            catch (Exception ex)
            {
                // 情境只是診斷用的加值資訊；Warning 不會進例外紀錄。
                requestLogger.LogWarning(ex, "Failed to set exception context for HTTP request.");
            }

            try
            {
                await next();
                stopwatch.Stop();

                requestLogger.LogInformation(
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // 走到這裡時驗證已經跑過、User 有值；但內層 UseExceptionContextUser 設定的帳號
                // 不會流回外層（AsyncLocal），所以記錄前在這裡補上，否則例外紀錄的帳號欄是空的。
                EnrichExceptionContextUser(context);

                requestLogger.LogError(
                    ex,
                    "HTTP {Method} {Path} failed after {ElapsedMilliseconds} ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        });

        return app;
    }

    /// <summary>
    /// 驗證之後把使用者與路由樣板補進系統例外紀錄的情境（0.4.122）。必須放在 <c>UseAuthorization()</c> 之後。
    /// </summary>
    public static WebApplication UseExceptionContextUser(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            EnrichExceptionContextUser(context);
            await next();
        });

        return app;
    }

    private static void EnrichExceptionContextUser(HttpContext context)
    {
        try
        {
            var accessor = context.RequestServices.GetRequiredService<ExceptionContextAccessor>();
            var current = accessor.Current;
            if (current is null)
            {
                return;
            }

            // 路由樣板（/api/Project/{id}）當頁面：同一支 API 不同編號才會併成同一筆。
            var enriched = current with { Page = ResolveRouteTemplate(context) ?? current.Page };

            var (account, userId) = RequestActorResolver.Resolve(context.User);
            if (userId is not null)
            {
                enriched = enriched with { Account = account, UserId = userId };
            }

            if (enriched != current)
            {
                accessor.Set(enriched);
            }
        }
        catch (Exception ex)
        {
            context.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(ApplicationBuilderExtensions))
                .LogWarning(ex, "Failed to add the authenticated user to the exception context.");
        }
    }

    private static string? ResolveRouteTemplate(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint { RoutePattern.RawText: { Length: > 0 } rawText })
        {
            return null;
        }

        return rawText.StartsWith('/') ? rawText : "/" + rawText;
    }

    public static WebApplication UseConfiguredLocalization(this WebApplication app)
    {
        var localizationOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>()
            .Value;

        app.UseRequestLocalization(localizationOptions);
        return app;
    }

    public static WebApplication UseConfiguredDownloadStaticFiles(this WebApplication app, SystemSettings systemSettings)
    {
        if (string.IsNullOrWhiteSpace(systemSettings.ExternalFileSystem.DownloadPath))
        {
            return app;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(systemSettings.ExternalFileSystem.DownloadPath),
            RequestPath = "/UploadFiles"
        });

        return app;
    }
}
