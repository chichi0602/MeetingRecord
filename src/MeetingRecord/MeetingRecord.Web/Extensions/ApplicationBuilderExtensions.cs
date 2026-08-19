using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Configuration;
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

    public static WebApplication UseHttpRequestLogging<TProgram>(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var requestLogger = context.RequestServices.GetRequiredService<ILogger<TProgram>>();
            var stopwatch = Stopwatch.StartNew();

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
