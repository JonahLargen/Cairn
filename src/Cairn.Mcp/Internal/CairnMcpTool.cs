using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cairn.Mcp.Internal;

/// <summary>
/// Shared plumbing for Cairn's MCP tools: resolves the HTTP request the call arrived on, loads the target
/// resource through its registration, and rebuilds the resource's link set with the caller's identity — the
/// same computation that decides what a hypermedia response would advertise.
/// </summary>
internal abstract class CairnMcpTool : McpServerTool
{
    protected CairnMcpTool(CairnMcpResourceRegistration registration) => Registration = registration;

    protected CairnMcpResourceRegistration Registration { get; }

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata => [];

    public sealed override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = request.Services
            ?? throw new InvalidOperationException("The MCP request carries no service provider; Cairn.Mcp tools require the ASP.NET Core HTTP transport (WithHttpTransport + MapMcp).");
        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext
            ?? throw new InvalidOperationException("No HTTP request is active; Cairn.Mcp tools require the ASP.NET Core HTTP transport (WithHttpTransport + MapMcp) so the caller's identity and base address are available.");

        string? id = null;
        if (Registration.RequiresId)
        {
            if (ArgumentOf(request.Params?.Arguments, "id") is not { } supplied || string.IsNullOrWhiteSpace(supplied))
            {
                return Error($"The 'id' argument identifying the {Registration.Name} is required.");
            }

            id = supplied;
        }

        var resource = await Registration.LoadAsync(id, services, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            return Error(Registration.RequiresId
                ? $"No {Registration.Name} with id '{id}' was found."
                : $"The {Registration.Name} resource is not available.");
        }

        var linkSet = await BuildLinkSetAsync(resource, services, cancellationToken).ConfigureAwait(false);
        return await InvokeCoreAsync(resource, linkSet, request, httpContext, services, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the tool once the resource is loaded and its link set is computed for the current caller.</summary>
    protected abstract ValueTask<CallToolResult> InvokeCoreAsync(
        object resource,
        LinkSet linkSet,
        RequestContext<CallToolRequestParams> request,
        HttpContext httpContext,
        IServiceProvider services,
        CancellationToken cancellationToken);

    protected static CallToolResult Error(string message)
        => new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    protected static CallToolResult Success(string message)
        => new() { Content = [new TextContentBlock { Text = message }] };

    /// <summary>Reads a tool-call argument as text: strings verbatim, other JSON values as their raw text.</summary>
    protected static string? ArgumentOf(IDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }

    // Mirrors how Cairn.AspNetCore builds the per-request LinkContext for serialization, so the affordances a
    // tool sees are exactly the affordances a hypermedia response to this caller would advertise.
    private static async ValueTask<LinkSet> BuildLinkSetAsync(object resource, IServiceProvider services, CancellationToken cancellationToken)
    {
        var engine = services.GetService<ILinkEngine>()
            ?? throw new InvalidOperationException("Cairn is not registered. Call services.AddCairn(...) and register a link configuration for every resource exposed through WithCairnAffordances.");

        var context = new LinkContext(
            services.GetRequiredService<ILinkUrlResolver>(),
            services.GetRequiredService<ILinkAuthorizer>(),
            services.GetRequiredService<AspNetCore.CairnOptions>().Mode,
            services,
            cancellationToken);

        return await engine.BuildAsync(resource, context, cancellationToken).ConfigureAwait(false);
    }
}
