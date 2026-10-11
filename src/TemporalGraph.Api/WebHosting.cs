using Microsoft.Extensions.FileProviders;
using Path = System.IO.Path;

namespace TemporalGraph.Api;

/// <summary>
/// The API can also host the built web app (<c>npm run build</c> → <c>web/dist</c>), so one process serves the page, its
/// assets and <c>/graphql</c>. Client-side routes (<c>/book/&lt;id&gt;/3d?ch=7</c>, …) fall back to <c>index.html</c>, so a reload
/// or a pasted link opens the right view; a missing file (a path whose last segment has an extension) stays a 404.
/// Under <c>aspire run</c> the Vite dev server serves the page instead (its own history fallback); nothing changes there.
/// </summary>
public static class WebHosting
{
    /// <summary>
    /// The folder to serve: <c>Web:Root</c> (relative to the content root, or absolute), else <c>wwwroot</c> under the content root;
    /// null when that folder has no <c>index.html</c> (the default for a plain <c>dotnet run</c> or <c>aspire run</c>).
    /// </summary>
    public static string? Root(IConfiguration config, string contentRoot)
    {
        var configured = config["Web:Root"]?.Trim();
        var root = Path.GetFullPath(string.IsNullOrEmpty(configured) ? Path.Combine(contentRoot, "wwwroot") : Path.Combine(contentRoot, configured));
        return File.Exists(Path.Combine(root, "index.html")) ? root : null;
    }

    public static void MapWebApp(this WebApplication app)
    {
        var root = Root(app.Configuration, app.Environment.ContentRootPath);
        if (root is null) return;
        var files = new StaticFileOptions { FileProvider = new PhysicalFileProvider(root) };
        app.UseStaticFiles(files);
        // {*path:nonfile}: /graphql and /health are matched first (explicit endpoints); asset-like paths aren't caught.
        app.MapFallbackToFile("index.html", files);
        app.Logger.LogInformation("Serving the web app from {Root} (history fallback to index.html)", root);
    }
}
