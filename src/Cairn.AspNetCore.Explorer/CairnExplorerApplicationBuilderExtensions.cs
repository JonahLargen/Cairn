using System.Reflection;
using System.Text;
using System.Text.Json;
using Cairn.AspNetCore.Explorer.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Explorer;

/// <summary>Extension methods for mounting the Cairn HAL Explorer into the request pipeline.</summary>
public static class CairnExplorerApplicationBuilderExtensions
{
    private const string ConfigPlaceholder = "__CAIRN_EXPLORER_CONFIG__";
    private const string TemplateResourceName = "Cairn.AspNetCore.Explorer.Assets.explorer.html";

    /// <summary>
    /// Serves the HAL Explorer — a browsable, self-documenting UI for the API — at
    /// <see cref="CairnExplorerOptions.Path"/> (default <c>/explorer</c>). The UI is a single embedded HTML
    /// document that makes no external request; it fetches the API on the same origin with the caller's
    /// credentials, so the links and actions it shows are exactly what this caller is authorized to see.
    /// </summary>
    /// <remarks>
    /// The explorer is served in the <c>Development</c> environment only unless <see cref="CairnExplorerOptions.Enabled"/>
    /// is set — it exposes the whole API surface interactively. When disabled the pipeline is left unchanged and
    /// the path resolves as it otherwise would (typically a 404). The media types offered in the UI's
    /// <c>Accept</c> selector are read from the app's <see cref="CairnOptions"/>, so a customized
    /// <see cref="CairnMediaTypeOptions"/> is reflected automatically.
    /// </remarks>
    /// <param name="app">The application builder.</param>
    /// <param name="configure">Optional configuration of the mount path, entry point, title, and enablement.</param>
    /// <returns>The same <see cref="IApplicationBuilder"/> instance, for chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseCairnExplorer(this IApplicationBuilder app, System.Action<CairnExplorerOptions>? configure = null)
    {
        System.ArgumentNullException.ThrowIfNull(app);

        var options = new CairnExplorerOptions();
        configure?.Invoke(options);

        // The host always registers IHostEnvironment; when the caller hasn't pinned Enabled, serve in
        // Development only — the explorer exposes the whole API surface interactively.
        var enabled = options.Enabled ?? app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
        if (!enabled)
        {
            return app;
        }

        var mediaTypes = app.ApplicationServices.GetService<CairnOptions>()?.MediaTypes ?? new CairnMediaTypeOptions();
        var config = new CairnExplorerConfig(
            options.EntryPoint,
            options.Title,
            new CairnExplorerMediaTypes(mediaTypes.HalForms, mediaTypes.Hal, mediaTypes.Cairn, mediaTypes.Json));

        // The default JSON encoder escapes '<', '>' and '&', so the serialized config is safe to inline inside
        // the <script> element even if a title or entry point carries markup.
        var json = JsonSerializer.Serialize(config, CairnExplorerJsonContext.Default.CairnExplorerConfig);
        var html = Encoding.UTF8.GetBytes(LoadTemplate().Replace(ConfigPlaceholder, json, System.StringComparison.Ordinal));

        // Register the terminal middleware without UseMiddleware<T>'s reflection so the package stays
        // trim-/AOT-analysis clean under IsAotCompatible.
        var path = options.Path;
        return app.Use(next => new CairnExplorerMiddleware(next, path, html).Invoke);
    }

    private static string LoadTemplate()
    {
        var assembly = typeof(CairnExplorerApplicationBuilderExtensions).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(TemplateResourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
