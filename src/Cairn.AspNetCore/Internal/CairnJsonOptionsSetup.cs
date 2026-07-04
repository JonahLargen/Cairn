using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Wires Cairn's link-injection contract modifier into the JSON options as a post-configuration step, so it
/// always wraps the final <see cref="JsonSerializerOptions.TypeInfoResolver"/> — including one the app assigns
/// after <c>AddCairn</c> (the standard source-generator setup), which would silently discard a modifier added
/// at configure time. Also appends <see cref="CairnJsonContext"/> so Cairn's own wire types resolve under a
/// source-gen-only resolver.
/// </summary>
internal sealed class CairnJsonOptionsSetup(IHttpContextAccessor accessor, CairnOptions options, ILinkConfigProvider configs) :
    IPostConfigureOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>,
    IPostConfigureOptions<Microsoft.AspNetCore.Mvc.JsonOptions>
{
    // Minimal APIs (and WriteAsJsonAsync) serialize through Http.Json options.
    public void PostConfigure(string? name, Microsoft.AspNetCore.Http.Json.JsonOptions options)
        => Apply(options.SerializerOptions);

    // MVC controllers serialize through the System.Text.Json output formatter, which reads Mvc.JsonOptions.
    // Configuring it is harmless when MVC is not in use.
    public void PostConfigure(string? name, Microsoft.AspNetCore.Mvc.JsonOptions options)
        => Apply(options.JsonSerializerOptions);

    private void Apply(JsonSerializerOptions serializer)
    {
        var modifier = new CairnLinkInjectionModifier(accessor, options, configs);
        serializer.TypeInfoResolver = JsonTypeInfoResolver.Combine(
                serializer.TypeInfoResolver ?? DefaultResolver(),
                CairnJsonContext.Default)
            .WithAddedModifier(modifier.Modify);
    }

    // The reflection-based resolver backstops a host that has no resolver at all — only when reflection
    // serialization is enabled (the JIT default). In a source-gen-only app (Native AOT / PublishTrimmed
    // with the feature switch off) it stays null and Combine skips it, leaving whatever the host
    // configured plus Cairn's own context.
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Guarded by JsonSerializer.IsReflectionEnabledByDefault; the feature-guard attribute that teaches the analyzer about this property only exists on net9.0+, so the guard is not recognized when compiling for net8.0.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Guarded by JsonSerializer.IsReflectionEnabledByDefault, which is always false under Native AOT.")]
    [ExcludeFromCodeCoverage(Justification = "The null arm requires a host with reflection serialization disabled (Native AOT or the feature switch); the test host always has it enabled.")]
    private static IJsonTypeInfoResolver? DefaultResolver()
        => JsonSerializer.IsReflectionEnabledByDefault ? new DefaultJsonTypeInfoResolver() : null;
}
