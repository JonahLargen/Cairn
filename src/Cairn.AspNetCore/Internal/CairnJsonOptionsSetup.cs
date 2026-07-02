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
internal sealed class CairnJsonOptionsSetup(IHttpContextAccessor accessor) :
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
        var modifier = new CairnLinkInjectionModifier(accessor);
        serializer.TypeInfoResolver = JsonTypeInfoResolver.Combine(
                serializer.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver(),
                CairnJsonContext.Default)
            .WithAddedModifier(modifier.Modify);
    }
}
