using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cairn.Mcp.Internal;

/// <summary>
/// One declared affordance exposed as an MCP tool. Listing is filtered by the affordance's caller-only
/// authorization policy (see <see cref="CairnMcpListToolsFilter"/>); a call re-loads the resource and only
/// proceeds when the link engine still advertises the affordance to this caller — the same state and
/// authorization gates a hypermedia response applies.
/// </summary>
internal sealed class CairnAffordanceTool : CairnMcpTool
{
    private readonly AffordanceSchema _schema;

    public CairnAffordanceTool(CairnMcpResourceRegistration registration, AffordanceSchema schema, JsonSerializerOptions serializer)
        : base(registration)
    {
        _schema = schema;
        ProtocolTool = new Tool
        {
            Name = CairnMcpToolName.For(registration.Name, schema.Name.Value),
            Title = schema.Title,
            Description = Describe(registration, schema),
            InputSchema = CairnMcpInputSchema.Build(
                registration.RequiresId ? CairnMcpToolName.IdDescription(registration.Name) : null,
                schema.Input,
                serializer),
        };
    }

    public override Tool ProtocolTool { get; }

    /// <summary>
    /// The caller-only policy deciding whether the tool is listed for the current user, or
    /// <see langword="null"/> when listing cannot be decided without a resource instance (no policy, or a
    /// resource-based one). The empty string means the host's default policy.
    /// </summary>
    public string? CallerPolicy => _schema.PolicyIsResourceBased ? null : _schema.Policy;

    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        object resource,
        LinkSet linkSet,
        RequestContext<CallToolRequestParams> request,
        HttpContext httpContext,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        Affordance? affordance = null;
        foreach (var candidate in linkSet.Affordances)
        {
            if (candidate.Name == _schema.Name)
            {
                affordance = candidate;
                break;
            }
        }

        if (affordance is null)
        {
            var available = linkSet.Affordances.Count == 0
                ? "none"
                : string.Join(", ", linkSet.Affordances.Select(a => $"'{a.Name.Value}'"));
            return Error(
                $"The '{_schema.Name.Value}' action is not currently available on this {Registration.Name} — " +
                $"its state or authorization gates exclude it for this caller. Currently available actions: {available}.");
        }

        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Params?.Arguments is { } supplied)
        {
            foreach (var argument in supplied)
            {
                if (!Registration.RequiresId || !string.Equals(argument.Key, "id", StringComparison.Ordinal))
                {
                    arguments[argument.Key] = argument.Value;
                }
            }
        }

        var invoker = services.GetRequiredService<ICairnMcpAffordanceInvoker>();
        var result = await invoker.InvokeAsync(
            new CairnMcpAffordanceCall { Affordance = affordance, HttpContext = httpContext, Arguments = arguments },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var failure = $"The '{_schema.Name.Value}' action failed with HTTP {result.StatusCode}.";
            return Error(string.IsNullOrEmpty(result.Content) ? failure : $"{failure}\n{result.Content}");
        }

        return Success(string.IsNullOrEmpty(result.Content)
            ? $"The '{_schema.Name.Value}' action succeeded with HTTP {result.StatusCode}."
            : result.Content);
    }

    private static string Describe(CairnMcpResourceRegistration registration, AffordanceSchema schema)
    {
        var description = new StringBuilder();
        description.Append(System.Globalization.CultureInfo.InvariantCulture, $"Invokes the '{schema.Name.Value}' action on the {registration.Name} resource");
        if (schema.Title is not null)
        {
            description.Append(System.Globalization.CultureInfo.InvariantCulture, $": {schema.Title}");
        }

        description.Append('.');

        if (schema.HasCondition)
        {
            description.Append(" The action is only available while the resource is in an eligible state; calling it when unavailable returns an error listing the currently available actions.");
        }

        if (schema.Policy is not null)
        {
            description.Append(" The caller must satisfy an authorization policy.");
        }

        return description.ToString();
    }
}
