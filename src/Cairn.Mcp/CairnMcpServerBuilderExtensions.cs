using Cairn.Mcp.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Cairn.Mcp;

/// <summary>Exposes Cairn affordances as MCP tools on a Model Context Protocol server.</summary>
public static class CairnMcpServerBuilderExtensions
{
    /// <summary>
    /// Exposes the state- and authorization-gated affordances of the resources registered in
    /// <paramref name="configure"/> as MCP tools. Each declared affordance becomes a
    /// <c>{resource}_{affordance}</c> tool (plus a <c>{resource}_get</c> state-inspection tool):
    /// <c>tools/list</c> hides tools whose caller-only authorization policy the current user fails, and a call
    /// re-loads the resource and only proceeds when the link engine still advertises the affordance to this
    /// caller — the same gates a hypermedia response applies.
    /// </summary>
    /// <remarks>
    /// Requires the ASP.NET Core HTTP transport (<c>WithHttpTransport</c> + <c>MapMcp</c>) and a Cairn
    /// registration (<c>AddCairn</c>) with a link configuration for every resource exposed. The host stays in
    /// charge of authenticating the MCP endpoint itself (e.g. <c>RequireAuthorization()</c> on
    /// <c>MapMcp</c>'s route).
    /// </remarks>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="configure">Registers the resources to expose and tunes invocation behavior.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is null.</exception>
    public static IMcpServerBuilder WithCairnAffordances(this IMcpServerBuilder builder, Action<CairnMcpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);

        // Everything below is idempotent, so calling WithCairnAffordances more than once composes options
        // rather than duplicating tools or filters.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient(HttpCairnMcpAffordanceInvoker.HttpClientName);
        builder.Services.TryAddSingleton<ICairnMcpAffordanceInvoker, HttpCairnMcpAffordanceInvoker>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<McpServerOptions>, CairnMcpToolsSetup>());

        if (!builder.Services.Any(service => service.ServiceType == typeof(CairnMcpFilterMarker)))
        {
            builder.Services.AddSingleton<CairnMcpFilterMarker>();
            builder.WithRequestFilters(filters => filters.AddListToolsFilter(CairnMcpListToolsFilter.Create));
        }

        return builder;
    }

    /// <summary>Marks that the list-tools filter is registered, keeping repeated <see cref="WithCairnAffordances"/> calls idempotent.</summary>
    private sealed class CairnMcpFilterMarker;
}
