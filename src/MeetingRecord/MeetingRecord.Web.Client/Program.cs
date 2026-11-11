using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Client.Realtime;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// 同網域呼叫 Web API：登入 Cookie 由瀏覽器自動帶上；每個請求加防偽標頭（Cookie 路徑的 CSRF 防護，1.1.0）。
// 不用 IHttpClientFactory（要多下載一個套件），WASM 端一個頁籤一份 HttpClient 就夠。
builder.Services.AddScoped(_ => new HttpClient(new ClientHeaderHandler { InnerHandler = new HttpClientHandler() })
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<ApiClient>();

builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<NotificationApi>();
builder.Services.AddScoped<AccountApi>();
builder.Services.AddScoped<SystemApi>();
builder.Services.AddScoped<PageAccessApi>();
builder.Services.AddScoped<PermissionView>();
builder.Services.AddScoped<CategoryApi>();
builder.Services.AddScoped<TeamApi>();
builder.Services.AddScoped<PromptTemplateApi>();
builder.Services.AddScoped<UserApi>();
builder.Services.AddScoped<RoleApi>();
builder.Services.AddScoped<DashboardApi>();
builder.Services.AddScoped<AppHubConnection>();
builder.Services.AddScoped<MeetingRecord.Web.Components.Commons.INotificationSource, ApiNotificationSource>();
builder.Services.AddScoped<JobApi>();
builder.Services.AddScoped<TodoApi>();
builder.Services.AddScoped<MeetingRecord.Web.Components.Commons.IAiChatSource, AiChatApi>();
builder.Services.AddScoped<MeetingApi>();
builder.Services.AddScoped<UploadApi>();
builder.Services.AddScoped<ProjectApi>();
builder.Services.AddScoped<TodoExtractionApi>();
builder.Services.AddScoped<MeetingRecord.Web.Client.Uploads.ProjectAttachmentUploader>();
builder.Services.AddScoped<HelpApi>();

// 1.1.23：會議影音檔上傳由整個前端共用一份（WASM 的 Scoped 就是整個分頁），換 WASM 頁不中斷。
builder.Services.AddScoped<MeetingRecord.Web.Client.Uploads.MeetingUploadManager>();

// 1.1.17：前端互動（剪貼簿、下載、文字編輯器、待辦自動欄寬），與 Server 頁共用同一份類別（命名空間 MeetingRecord.Web.Services）。
builder.Services.AddScoped<MeetingRecord.Web.Services.ClipboardInterop>();
builder.Services.AddScoped<MeetingRecord.Web.Services.FileDownloadInterop>();
builder.Services.AddScoped<MeetingRecord.Web.Services.TextEditorInterop>();
builder.Services.AddScoped<MeetingRecord.Web.Services.TodoAutoFitInterop>();

// 1.1.30：使用說明頁的前端互動（/ 快速搜尋、高亮、錨點、問答串捲動），頁面搬到 WASM 後只有這裡用。
builder.Services.AddScoped<MeetingRecord.Web.Services.HelpViewInterop>();
builder.Services.AddScoped<MeetingRecord.Web.Components.Commons.IJobProgressSource, ApiJobProgressSource>();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAntDesign();

await builder.Build().RunAsync();
