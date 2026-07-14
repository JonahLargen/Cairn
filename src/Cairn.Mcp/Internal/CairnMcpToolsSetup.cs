using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Cairn.Mcp.Internal;

/// <summary>
/// Materializes the registered resources into MCP tools when the server's options are built: one tool per
/// declared affordance plus (optionally) a <c>{name}_get</c> tool per resource. Runs after the SDK's own
/// setup, so tools registered through <c>WithTools</c> coexist with Cairn's.
/// </summary>
internal sealed class CairnMcpToolsSetup : IConfigureOptions<McpServerOptions>
{
    private readonly IOptions<CairnMcpOptions> _options;
    private readonly IServiceProvider _services;

    public CairnMcpToolsSetup(IOptions<CairnMcpOptions> options, IServiceProvider services)
    {
        _options = options;
        _services = services;
    }

    public void Configure(McpServerOptions serverOptions)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);

        var options = _options.Value;
        if (options.Resources.Count == 0)
        {
            return;
        }

        var configs = _services.GetService<ILinkConfigProvider>()
            ?? throw new InvalidOperationException(
                "Cairn is not registered. Call services.AddCairn(...) — and register a link configuration for every resource passed to AddResource — before mapping the MCP server.");
        var serializer = _services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

        var tools = serverOptions.ToolCollection ?? [];
        foreach (var registration in options.Resources)
        {
            var config = configs.GetConfig(registration.ResourceType)
                ?? throw new InvalidOperationException(
                    $"No link configuration is registered for {registration.ResourceType.Name}. Register one with options.AddLinks(...) in AddCairn, or remove AddResource<{registration.ResourceType.Name}>(\"{registration.Name}\", ...).");

            if (config is not IDeclarationReportingConfig reporting)
            {
                throw new InvalidOperationException(
                    $"The link configuration for {registration.ResourceType.Name} ({config.GetType().Name}) does not implement {nameof(IDeclarationReportingConfig)}, so its declared affordances cannot be described as MCP tools.");
            }

            if (options.IncludeGetTools)
            {
                Add(tools, new CairnResourceGetTool(registration, serializer));
            }

            foreach (var schema in reporting.DeclaredAffordances)
            {
                Add(tools, new CairnAffordanceTool(registration, schema, serializer));
            }
        }

        serverOptions.ToolCollection = tools;
    }

    private static void Add(McpServerPrimitiveCollection<McpServerTool> tools, CairnMcpTool tool)
    {
        if (!tools.TryAdd(tool))
        {
            throw new InvalidOperationException(
                $"An MCP tool named '{tool.ProtocolTool.Name}' already exists. Tool names are '{{resource}}_{{affordance}}' (plus the reserved '{{resource}}_{CairnMcpToolName.GetSuffix}'); " +
                "rename the resource in AddResource, rename the colliding affordance, or set IncludeGetTools = false if a 'get' affordance collides with the state-inspection tool.");
        }
    }
}
