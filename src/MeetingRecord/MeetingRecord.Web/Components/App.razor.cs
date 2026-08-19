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

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

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
