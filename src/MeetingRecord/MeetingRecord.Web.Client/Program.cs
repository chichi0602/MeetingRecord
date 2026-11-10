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
builder.Services.AddScoped<AppHubConnection>();
builder.Services.AddScoped<MeetingRecord.Web.Components.Commons.INotificationSource, ApiNotificationSource>();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAntDesign();

await builder.Build().RunAsync();
