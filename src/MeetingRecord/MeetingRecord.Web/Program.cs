using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using AntDesign;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Repositories;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Components;
using MeetingRecord.Web.Components.Layout;
using MeetingRecord.Web.Configuration;
using MeetingRecord.Web.Extensions;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Localization;
using NLog;
using NLog.Web;
using System.Text;
using System.Text.Json;

namespace MeetingRecord.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            ILogger<Program>? logger = null;
            try
            {
                var builder = WebApplication.CreateBuilder(args);
                StartupSafetyValidator.Validate(builder.Configuration, builder.Environment.EnvironmentName);

                #region NLog 相關設定
                var nlogBasePrefixPath = builder.Configuration.GetValue<string>("NLog:BasePath");
                var baseNamespace = typeof(Program).Namespace ?? nameof(MeetingRecord.Web);

                string? nlogBasePath = null;
                if (!string.IsNullOrWhiteSpace(nlogBasePrefixPath))
                {
                    nlogBasePath = Path.Combine(nlogBasePrefixPath, baseNamespace);
                    Directory.CreateDirectory(nlogBasePath);

                    // 設置內部日誌記錄器
                    NLog.Common.InternalLogger.LogLevel = NLog.LogLevel.Info;
                    NLog.Common.InternalLogger.LogFile = Path.Combine(nlogBasePath, $"{baseNamespace}-nlog-internal.log");

                    // 設置變量到當前配置
                    if (LogManager.Configuration is not null)
                    {
                        LogManager.Configuration.Variables["BasePath"] = nlogBasePath;
                        LogManager.Configuration.Variables["LogFilenamePrefix"] = $"{baseNamespace}-logfile";
                    }
                }

                builder.Logging.ClearProviders();
                builder.Host.UseNLog();
                #endregion

                #region 系統使用服務
                // Add services to the container.
                builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents()
                .AddHubOptions(hubOptions =>
                {
                    // Blazor Server SignalR 預設 MaximumReceiveMessageSize 為 32KB；長文字（如大段
                    // 描述/摘要）由 AntDesign TextArea 同步回伺服器時會超過上限 → circuit 中斷 →
                    // 反覆重連（畫面閃爍）。提高至 10MB 以容納長內容。
                    hubOptions.MaximumReceiveMessageSize = 10 * 1024 * 1024;
                });

                builder.Services.AddControllers(options =>
                {
                    options.Filters.Add<ApiExceptionFilterAttribute>();
                });
                builder.Services.Configure<ApiBehaviorOptions>(options =>
                {
                    options.SuppressModelStateInvalidFilter = true;
                });
                //builder.Services.AddOpenApi();
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen(options =>
                {
                    options.SwaggerDoc("v1", new OpenApiInfo
                    {
                        Title = "MeetingRecord API",
                        Version = "v1",
                        Description = "內部管理系統腳手架 API v1"
                    });
                    options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, new OpenApiSecurityScheme
                    {
                        Name = "Authorization",
                        Type = SecuritySchemeType.Http,
                        Scheme = JwtBearerDefaults.AuthenticationScheme,
                        BearerFormat = "JWT",
                        In = ParameterLocation.Header,
                        Description = "請輸入 JWT Bearer token。"
                    });

                    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, null, null),
                            new List<string>()
                        }
                    });
                });
                builder.Services.AddAntDesign();
                builder.Services.AddConfiguredLocalization();
                builder.Services.AddConfiguredOptions(builder.Configuration);
                builder.Services.AddConfiguredCors(builder.Configuration);
                builder.Services.AddConfiguredRateLimiting();
                builder.Services.AddConfiguredHealthChecks();
                builder.Services.AddConfiguredCache(builder.Configuration);

                #region 加入使用 Cookie & JWT 認證需要的宣告
                builder.Services.Configure<CookiePolicyOptions>(options =>
                {
                    options.CheckConsentNeeded = context => true;
                    options.MinimumSameSitePolicy = Microsoft.AspNetCore.Http.SameSiteMode.None;
                });

                var jwtSettings = builder.Configuration
                    .GetSection(JwtSettings.SectionName)
                    .Get<JwtSettings>() ?? new JwtSettings();
                builder.Services
                    .AddOptions<JwtSettings>()
                    .Bind(builder.Configuration.GetSection(JwtSettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                var googleOAuthSettings = builder.Configuration
                    .GetSection(GoogleOAuthSettings.SectionName)
                    .Get<GoogleOAuthSettings>() ?? new GoogleOAuthSettings();
                builder.Services.Configure<GoogleOAuthSettings>(
                    builder.Configuration.GetSection(GoogleOAuthSettings.SectionName));

                var authenticationBuilder = builder.Services.AddAuthentication(MagicObjectHelper.CookieScheme)
                    .AddCookie(MagicObjectHelper.CookieScheme, options =>
                    {
                        options.Cookie.IsEssential = true;
                        options.LoginPath = "/Auths/Login";
                        options.LogoutPath = "/Auths/Logout";
                        options.AccessDeniedPath = "/Auths/Login";
                    })
                    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                        options.SaveToken = false;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = jwtSettings.Issuer,
                            ValidateAudience = true,
                            ValidAudience = jwtSettings.Audience,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
                            ValidateLifetime = true,
                            ClockSkew = TimeSpan.FromMinutes(jwtSettings.ClockSkewMinutes)
                        };
                        options.Events = new JwtBearerEvents
                        {
                            OnChallenge = async context =>
                            {
                                context.HandleResponse();
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                context.Response.ContentType = "application/json; charset=utf-8";

                                var result = ApiResult.UnauthorizedResult("未提供有效的 Bearer token。");
                                result.TraceId = context.HttpContext.TraceIdentifier;
                                await context.Response.WriteAsync(JsonSerializer.Serialize(result));
                            },
                            OnForbidden = async context =>
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                context.Response.ContentType = "application/json; charset=utf-8";

                                var result = ApiResult.ForbiddenResult("目前使用者沒有權限存取此 API。");
                                result.TraceId = context.HttpContext.TraceIdentifier;
                                await context.Response.WriteAsync(JsonSerializer.Serialize(result));
                            }
                        };
                    });

                #region Google OAuth2 第三方登入（僅在已設定時註冊）
                if (googleOAuthSettings.IsConfigured)
                {
                    authenticationBuilder
                        .AddCookie(MagicObjectHelper.ExternalCookieScheme, options =>
                        {
                            options.Cookie.Name = ".MeetingRecord.External";
                            options.Cookie.IsEssential = true;
                            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                        })
                        .AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
                        {
                            options.ClientId = googleOAuthSettings.ClientId;
                            options.ClientSecret = googleOAuthSettings.ClientSecret;
                            options.SignInScheme = MagicObjectHelper.ExternalCookieScheme;
                            options.CallbackPath = "/signin-google";
                            options.SaveTokens = false;
                        });
                }
                #endregion

                builder.Services.AddAuthorization();
                #endregion

                #region AutoMapper 使用的宣告
                builder.Services.AddAutoMapper(c =>
                {
                    var autoMapperLicenseKey = builder.Configuration["AutoMapper:LicenseKey"];
                    if (string.IsNullOrWhiteSpace(autoMapperLicenseKey) == false)
                    {
                        c.LicenseKey = autoMapperLicenseKey;
                    }

                    c.AddProfile<AutoMapping>();
                });
                #endregion

                #endregion

                #region 加入設定強型別注入宣告
                // LLM／STT 供應商設定：0.4.27 起由 AzureOpenAiTranscriptionProvider 實際使用轉錄段設定。
                builder.Services
                    .AddOptions<LlmSettings>()
                    .Bind(builder.Configuration.GetSection(LlmSettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
                #endregion

                #region 系統使用的目錄準備
                // 取得 系統設定物件 SystemSettings
                var systemSettings = builder.Configuration.GetSection(nameof(SystemSettings)).Get<SystemSettings>()
                    ?? new SystemSettings();
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.DatabasePath, "database");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.DownloadPath, "download");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.UploadPath, "upload");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.ProjectFilePath, "project file");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.MeetingMediaPath, "meeting media");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.MeetingTranscriptPath, "meeting transcript");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.AiChatPath, "ai chat");
                #endregion

                #region EF Core 宣告
                builder.Services.AddConfiguredDatabase(systemSettings);
                #endregion

                #region 客製服務註冊
                builder.Services.AddApplicationServices();
                builder.Services.AddTranscriptionServices();
                builder.Services.AddTextGenerationServices();
                #endregion

                var app = builder.Build();
                logger = app.Services.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Application host built successfully.");

                #region 非 Production 的設定提醒（不中止啟動）
                // StartupSafetyValidator.Validate 在 NLog 接管之前就跑完，記不了 log；
                // 開發環境的設定問題（例如沒裝 FFmpeg）因此改在這裡提醒，避免拖到轉錄失敗才發現。
                if (!app.Environment.IsProduction())
                {
                    foreach (var warning in StartupSafetyValidator.GetDevelopmentWarnings(app.Configuration))
                    {
                        logger.LogWarning("Startup configuration warning. {Warning}", warning);
                    }
                }
                #endregion

                var bootstrapSettings = app.Configuration
                    .GetSection(nameof(BootstrapSettings))
                    .Get<BootstrapSettings>() ?? new BootstrapSettings();

                #region 資料庫的 Migration
                //if (!app.Environment.IsDevelopment())
                {
                    using var scope = app.Services.CreateScope();
                    using var dbContext = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
                    logger.LogInformation("Ensuring database is ready.");
                    if (dbContext.Database.GetMigrations().Any())
                    {
                        dbContext.Database.Migrate();
                        logger.LogInformation("Database migrations applied successfully.");
                    }
                    else
                    {
                        dbContext.Database.EnsureCreated();
                        logger.LogInformation("Database created because no migrations were found.");
                    }

                    RoleView? roleViewItemNew = null;

                    #region 是否有存在的角色檢視定義
                    var roleViewItem = dbContext.RoleView
                        .FirstOrDefault(x => x.Name == MagicObjectHelper.預設角色);
                    RolePermissionService RolePermissionService = scope
                        .ServiceProvider
                        .GetRequiredService<RolePermissionService>();
                    var allPermissionJson = RolePermissionService
                        .GetRolePermissionAllNameToJson();
                    if (roleViewItem == null)
                    {
                        roleViewItemNew = new RoleView()
                        {
                            Name = MagicObjectHelper.預設角色,
                            TabViewJson = allPermissionJson
                        };
                        dbContext.RoleView.Add(roleViewItemNew);
                        dbContext.SaveChanges();
                        logger.LogInformation("Seeded default role view.");
                    }
                    else
                    {
                        roleViewItem.TabViewJson = allPermissionJson;
                        dbContext.SaveChanges();
                        logger.LogDebug("Updated existing default role view.");
                    }
                    #endregion

                    #region 產生預設帳號
                    var support = dbContext.MyUser
                        .FirstOrDefault(x => x.Account == bootstrapSettings.SupportAccount);

                    if (support == null)
                    {
                        support = new MyUser()
                        {
                            Account = bootstrapSettings.SupportAccount,
                            Name = bootstrapSettings.SupportName,
                            Email = bootstrapSettings.SupportEmail,
                            IsAdmin = true,
                            Salt = Guid.NewGuid().ToString(),
                            Status = true,
                            RoleViewId = (roleViewItemNew ?? roleViewItem)!.Id,
                        };
                        support.Password =
                            SecurePasswordHasher.HashPassword(bootstrapSettings.SupportPassword);

                        dbContext.MyUser.Add(support);
                        dbContext.SaveChanges();
                        logger.LogInformation("Seeded default support user.");
                    }
                    else
                    {
                        if (SecurePasswordHasher.VerifyPassword(bootstrapSettings.SupportPassword, support.Password, support.Salt)
                            != PasswordVerificationOutcome.Success)
                        {
                            support.Password =
                                SecurePasswordHasher.HashPassword(bootstrapSettings.SupportPassword);
                        }
                        support.IsAdmin = true;
                        if (roleViewItemNew != null)
                            support.RoleViewId = roleViewItemNew.Id;
                        else
                            support.RoleViewId = roleViewItem!.Id;
                        dbContext.SaveChanges();
                        logger.LogDebug("Updated existing support user seed data.");
                    }
                    #endregion

                    #region RBAC 回填（將既有權限資料填入新關聯表，冪等；失敗不中止啟動）
                    try
                    {
                        var rbacBackfill = scope.ServiceProvider.GetRequiredService<IRbacBackfillService>();
                        rbacBackfill.RunAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "RBAC backfill failed at startup.");
                    }
                    #endregion

                    #region 轉錄狀態修復（轉錄佇列不持久化，重啟後殘留的「待處理」與「處理中」都不會有人接手）
                    try
                    {
                        // Pending 也要一併復原：佇列不持久化，重啟後「已入列但還沒被 worker 撿走」的
                        // 紀錄同樣沒有人會接手，只掃 Processing 會讓它們永遠卡在「待處理」。
                        var interrupted = dbContext.Meeting
                            .Where(x => x.TranscriptionStatus == TranscriptionStatus.Processing
                                     || x.TranscriptionStatus == TranscriptionStatus.Pending)
                            .ToList();

                        if (interrupted.Count > 0)
                        {
                            foreach (var meeting in interrupted)
                            {
                                meeting.TranscriptionStatus = TranscriptionStatus.Failed;
                                meeting.TranscriptionError = "應用程式重啟導致轉錄中斷，請重新執行轉錄。";
                                meeting.TranscriptionCompletedAt = DateTime.Now;
                                meeting.UpdatedAt = DateTime.Now;
                            }

                            dbContext.SaveChanges();
                            logger.LogWarning(
                                "Reset interrupted transcription jobs at startup. Count={Count}",
                                interrupted.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to reset interrupted transcription jobs at startup.");
                    }
                    #endregion

                    #region 草稿生成狀態修復（生成佇列同樣不持久化，殘留的「待處理」與「生成中」都不會有人接手）
                    try
                    {
                        // 同轉錄：Pending 代表已入列但還沒開工，重啟後一樣沒有人接手。
                        var interruptedDrafts = dbContext.Meeting
                            .Where(x => x.DraftStatus == DraftStatus.Processing
                                     || x.DraftStatus == DraftStatus.Pending)
                            .ToList();

                        if (interruptedDrafts.Count > 0)
                        {
                            foreach (var meeting in interruptedDrafts)
                            {
                                meeting.DraftStatus = DraftStatus.Failed;
                                meeting.DraftError = "應用程式重啟導致會議紀錄生成中斷，請重新產生。";
                                meeting.DraftCompletedAt = DateTime.Now;
                                meeting.UpdatedAt = DateTime.Now;
                            }

                            dbContext.SaveChanges();
                            logger.LogWarning(
                                "Reset interrupted draft generation jobs at startup. Count={Count}",
                                interruptedDrafts.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to reset interrupted draft generation jobs at startup.");
                    }
                    #endregion
                }
                #endregion

                #region 註冊中介軟體
                // Configure the HTTP request pipeline.
                if (!app.Environment.IsDevelopment())
                {
                    app.UseExceptionHandler("/Error");
                    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                    app.UseHsts();
                }

                app.UseConfiguredForwardedHeaders();
                app.UseConfiguredSwagger(logger);
                app.UseHttpRequestLogging<Program>();

                app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
                app.UseHttpsRedirection();

                app.UseConfiguredLocalization();
                app.UseConfiguredCors();
                app.UseRateLimiter();

                app.UseAntiforgery();

                app.MapStaticAssets();

                #region 綁定靜態資源
                app.UseConfiguredDownloadStaticFiles(systemSettings);
                #endregion

                app.UseAuthentication();
                app.UseAuthorization();

                app.MapControllers()
                    .RequireRateLimiting("api");
                app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("live")
                });
                app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("ready")
                });
                app.MapRazorComponents<App>()
                    .AddInteractiveServerRenderMode();
                #endregion

                logger.LogInformation("Application startup completed. Listening for requests.");
                app.Run();

                void EnsureDirectoryExists(string? directoryPath, string directoryName)
                {
                    if (string.IsNullOrWhiteSpace(directoryPath))
                    {
                        return;
                    }

                    if (Directory.Exists(directoryPath))
                    {
                        return;
                    }

                    Directory.CreateDirectory(directoryPath);
                    logger?.LogInformation("Created {DirectoryName} directory at {DirectoryPath}", directoryName, directoryPath);
                }
            }
            catch (Exception ex)
            {
                if (logger != null)
                    logger.LogError(ex, "Stopped program because of an exception");
                throw;
            }
            finally
            {
                if(logger!=null)
                    logger.LogInformation("Application is shutting down.");
                LogManager.Shutdown();
            }
        }
    }
}
