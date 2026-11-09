using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MeetingRecord.Web.Components
{
    public partial class App
    {
        private readonly ILogger<App> logger;

        public App(ILogger<App> logger)
        {
            this.logger = logger;
        }

        /// <summary>WASM 頁不預先算繪：跟 Server 頁一致（Server 頁也關掉預先算繪），也避免伺服器端先畫一次再被瀏覽器重畫。</summary>
        private static readonly IComponentRenderMode WasmRenderMode = new InteractiveWebAssemblyRenderMode(prerender: false);

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        /// <summary>這個請求的路徑已遷移到 WASM（<see cref="MeetingRecord.Share.Helpers.WasmRoutes"/>，1.1.1）。</summary>
        private bool IsWasmPage => MeetingRecord.Share.Helpers.WasmRoutes.IsWasm(HttpContext.Request.Path.Value);

        public IComponentRenderMode? RenderModeForPage()
        {
            var path = HttpContext.Request.Path.Value ?? "/";
            var renderMode = HttpContext.Request.Path.StartsWithSegments("/Auths")
                ? null
                : new InteractiveServerRenderMode(prerender: false);

            logger.LogDebug("Resolved render mode for Path={Path}. InteractiveServer={IsInteractiveServer}", path, renderMode is not null);
            return renderMode;
        }
    }
}
