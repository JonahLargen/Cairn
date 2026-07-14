using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cairn.Mcp.Internal;

/// <summary>
/// The per-resource state-inspection tool (<c>{name}_get</c>): returns the resource's current state together
/// with its links and the actions the link engine currently advertises to this caller — how an agent discovers
/// what it can do before invoking an action tool.
/// </summary>
internal sealed class CairnResourceGetTool : CairnMcpTool
{
    private readonly JsonSerializerOptions _serializer;

    public CairnResourceGetTool(CairnMcpResourceRegistration registration, JsonSerializerOptions serializer)
        : base(registration)
    {
        _serializer = serializer;
        ProtocolTool = new Tool
        {
            Name = CairnMcpToolName.For(registration.Name, CairnMcpToolName.GetSuffix),
            Description =
                $"Reads the current state of the {registration.Name} resource, its links, and the actions currently " +
                "available to the caller. Call this to discover what the resource allows before invoking an action tool.",
            InputSchema = CairnMcpInputSchema.Build(
                registration.RequiresId ? CairnMcpToolName.IdDescription(Registration.Name) : null,
                input: null,
                serializer),
        };
    }

    public override Tool ProtocolTool { get; }

    protected override ValueTask<CallToolResult> InvokeCoreAsync(
        object resource,
        LinkSet linkSet,
        RequestContext<CallToolRequestParams> request,
        HttpContext httpContext,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // Serializing through the host's resolved contract keeps wire names consistent with the API (and stays
        // trim-safe); Cairn's own contract modifier no-ops here because nothing was recorded for the instance.
        var payload = new JsonObject
        {
            ["resource"] = JsonSerializer.SerializeToNode(resource, _serializer.GetTypeInfo(resource.GetType())),
            ["links"] = Links(linkSet),
            ["actions"] = Actions(linkSet),
        };

        return new(Success(payload.ToJsonString()));
    }

    private static JsonArray Links(LinkSet linkSet)
    {
        var links = new JsonArray();
        foreach (var link in linkSet.Links)
        {
            var entry = new JsonObject { ["rel"] = link.Relation.Value, ["href"] = link.Href };
            if (link.Title is not null)
            {
                entry["title"] = link.Title;
            }

            links.Add((JsonNode)entry);
        }

        return links;
    }

    private static JsonArray Actions(LinkSet linkSet)
    {
        var actions = new JsonArray();
        foreach (var affordance in linkSet.Affordances)
        {
            var entry = new JsonObject { ["name"] = affordance.Name.Value, ["method"] = affordance.Method, ["href"] = affordance.Href };
            if (affordance.Title is not null)
            {
                entry["title"] = affordance.Title;
            }

            actions.Add((JsonNode)entry);
        }

        return actions;
    }
}
