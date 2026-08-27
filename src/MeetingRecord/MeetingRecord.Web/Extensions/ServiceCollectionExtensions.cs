using AntDesign;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Repositories;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.BackgroundServices;
using MeetingRecord.Web.Caching;
using MeetingRecord.Web.Components.Layout;
using MeetingRecord.Web.Configuration;
using MeetingRecord.Web.Health;
using MeetingRecord.Web.Localization;
using System.Globalization;
using System.Threading.RateLimiting;

namespace MeetingRecord.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredLocalization(this IServiceCollection services)
    {
        services.AddLocalization();

        var supportedCultures = new[]
        {
            new CultureInfo("zh-TW"),
            new CultureInfo("en-US")
        };

        var defaultCulture = supportedCultures[0];

        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.DefaultRequestCulture = new RequestCulture(defaultCulture);
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;

            options.RequestCultureProviders = new List<IRequestCultureProvider>
            {
                new AcceptLanguageHeaderRequestCultureProvider()
            };
        });

        LocaleProvider.SetLocale("zh-TW", AntDesignLocaleFactory.Create("zh-TW"));
        LocaleProvider.DefaultLanguage = defaultCulture.Name;

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<SystemStartupState>();
        services.AddScoped<IHealthLogReader, HealthLogReader>();
        services.AddScoped<ISystemHealthService, SystemHealthService>();
        services.AddScoped<AuthenticationStateHelper>();
        services.AddScoped<CurrentUserService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<ITotpService, TotpService>();
        services.AddScoped<IRbacBackfillService, RbacBackfillService>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<IRbacWriteService, RbacWriteService>();
        services.AddScoped<IEffectiveTeamResolver, EffectiveTeamResolver>();
        services.AddScoped<MyUserServiceLogin>();
        services.AddScoped<ExternalLoginService>();
        services.AddScoped<SidebarMenuService>();
        services.AddScoped<RolePermissionService>();
        services.AddScoped<RoleViewService>();
        services.AddScoped<MyUserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<ProjectRepository>();
        services.AddScoped<CategoryService>();
        services.AddScoped<CategoryRepository>();
        services.AddScoped<TeamService>();
        services.AddScoped<TeamRepository>();
        services.AddScoped<PromptTemplateService>();
        services.AddScoped<PromptTemplateRepository>();
        services.AddScoped<MeetingService>();
        services.AddScoped<MeetingRepository>();
        services.AddScoped<MeetingFileStore>();
        services.AddHttpContextAccessor();
        services.AddScoped<IRecordAccessScopeProvider, RecordAccessScopeProvider>();
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, MeetingRecord.Web.Components.ApplicationCircuitHandler>();

        return services;
    }

    /// <summary>
    /// 語音轉錄相關註冊：行程內佇列（Singleton）、背景 worker、供應商實作與其具名 HttpClient。
    /// 新增其他廠商時，只要多註冊一個 <see cref="ITranscriptionProvider"/> 實作即可。
    /// </summary>
    public static IServiceCollection AddTranscriptionServices(this IServiceCollection services)
    {
        services.AddSingleton<ITranscriptionQueue, TranscriptionQueue>();
        services.AddHostedService<TranscriptionBackgroundService>();

        services.AddScoped<IMediaConverter, FfmpegMediaConverter>();
        services.AddScoped<ITranscriptionProvider, AzureOpenAiTranscriptionProvider>();
        services.AddScoped<TranscriptionJobRunner>();

        // 轉錄是長時間請求（單段 15 分鐘音訊），預設的 100 秒逾時明顯不夠。
        services.AddHttpClient(AzureOpenAiTranscriptionProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        return services;
    }

    public static IServiceCollection AddConfiguredOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SystemSettings>(configuration.GetSection(nameof(SystemSettings)));
        services.Configure<SecuritySettings>(configuration.GetSection(SecuritySettings.SectionName));
        services.Configure<CorsSettings>(configuration.GetSection(CorsSettings.SectionName));
        services.Configure<SwaggerSettings>(configuration.GetSection(SwaggerSettings.SectionName));
        services.Configure<CacheSettings>(configuration.GetSection(CacheSettings.SectionName));
        services.Configure<MediaSettings>(configuration.GetSection(MediaSettings.SectionName));

        return services;
    }

    public static IServiceCollection AddConfiguredDatabase(this IServiceCollection services, SystemSettings systemSettings)
    {
        services.AddDbContext<BackendDBContext>(options =>
        {
            var sqliteConnectionString = MagicObjectHelper.GetSQLiteConnectionString(systemSettings.ExternalFileSystem.DatabasePath);
            options.UseSqlite(sqliteConnectionString);
        }, ServiceLifetime.Scoped);

        return services;
    }

    public static IServiceCollection AddConfiguredCache(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CacheSettings.SectionName).Get<CacheSettings>() ?? new CacheSettings();

        switch (settings.GetProvider())
        {
            case CacheProvider.Redis:
                if (string.IsNullOrWhiteSpace(settings.RedisConnection))
                {
                    throw new InvalidOperationException("CacheSettings:RedisConnection 不可為空白。");
                }

                services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = settings.RedisConnection;
                    options.InstanceName = settings.InstanceName;
                });
                break;

            case CacheProvider.Memory:
                services.AddDistributedMemoryCache();
                break;

            default:
                throw new InvalidOperationException($"不支援的快取 provider：{settings.Provider}");
        }

        services.AddSingleton<ICacheService, DistributedCacheService>();

        return services;
    }

    public static IServiceCollection AddConfiguredCors(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
        services.AddCors(options =>
        {
            options.AddPolicy("ConfiguredCors", policy =>
            {
                if (settings.AllowedOrigins.Length == 0)
                {
                    policy.SetIsOriginAllowed(_ => false);
                    return;
                }

                policy
                    .WithOrigins(settings.AllowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        return services;
    }

    public static IServiceCollection AddConfiguredRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("api", limiterOptions =>
            {
                limiterOptions.PermitLimit = 120;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueLimit = 0;
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
        });

        return services;
    }

    public static IServiceCollection AddConfiguredHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        return services;
    }
}
