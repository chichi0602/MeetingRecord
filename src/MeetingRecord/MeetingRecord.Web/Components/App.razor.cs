using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Components
{
    public partial class App
    {
        /// <summary>WASM 頁不預先算繪：避免伺服器端先畫一次再被瀏覽器重畫。</summary>
        private static readonly IComponentRenderMode WasmRenderMode = new InteractiveWebAssemblyRenderMode(prerender: false);

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [Inject]
        private NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        private IOptions<SystemSettings> SystemOptions { get; set; } = default!;

        /// <summary>
        /// 這個請求的頁面在瀏覽器執行（1.1.45 起是 <see cref="MeetingRecord.Share.Helpers.ServerPageRoutes"/> 以外的全部）。
        /// 其餘（登入、登出、錯誤頁）由伺服器靜態算繪，不帶互動模式。
        /// </summary>
        private bool IsWasmPage => !MeetingRecord.Share.Helpers.ServerPageRoutes.IsServerPage(HttpContext.Request.Path.Value);

        /// <summary>載入畫面上的系統名稱（1.1.58）。</summary>
        private string SystemName => SystemOptions.Value.SystemInformation.SystemName;

        /// <summary>
        /// 1.1.58：未登入開 WASM 頁，伺服器直接導到登入頁並帶 ReturnUrl。
        /// 以前要等瀏覽器下載完整個 WASM、問 Session API 拿到 401 才被導走，第一次開網址會空白十幾秒。
        /// 缺檔的 404 重新執行到 /not-found 時不導（要維持 404）。
        /// </summary>
        protected override void OnInitialized()
        {
            if (IsWasmPage
                && HttpContext.User.Identity?.IsAuthenticated != true
                && HttpContext.Features.Get<IStatusCodeReExecuteFeature>() is null)
            {
                var returnUrl = HttpContext.Request.Path.Value + HttpContext.Request.QueryString.Value;
                NavigationManager.NavigateTo($"/Auths/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
            }
        }
    }
}
