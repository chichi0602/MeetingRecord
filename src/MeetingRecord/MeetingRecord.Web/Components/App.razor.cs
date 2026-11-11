using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MeetingRecord.Web.Components
{
    public partial class App
    {
        /// <summary>WASM 頁不預先算繪：避免伺服器端先畫一次再被瀏覽器重畫。</summary>
        private static readonly IComponentRenderMode WasmRenderMode = new InteractiveWebAssemblyRenderMode(prerender: false);

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        /// <summary>
        /// 這個請求的頁面在瀏覽器執行（1.1.45 起是 <see cref="MeetingRecord.Share.Helpers.ServerPageRoutes"/> 以外的全部）。
        /// 其餘（登入、登出、錯誤頁）由伺服器靜態算繪，不帶互動模式。
        /// </summary>
        private bool IsWasmPage => !MeetingRecord.Share.Helpers.ServerPageRoutes.IsServerPage(HttpContext.Request.Path.Value);
    }
}
