using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cairn.Mcp.Internal;

/// <summary>
/// Filters <c>tools/list</c> by each affordance tool's caller-only authorization policy, so an agent only sees
/// the actions its user could ever be offered — the listing counterpart of the per-response gating hypermedia
/// applies. State (<c>When</c>) and resource-based policy gates need an instance and are enforced at call time
/// instead.
/// </summary>
internal static class CairnMcpListToolsFilter
{
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> Create(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next)
        => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            if (result.Tools is not { Count: > 0 } listed || context.Server.ServerOptions.ToolCollection is not { } tools)
            {
                return result;
            }

            List<Tool>? kept = null;
            Dictionary<string, bool>? decisions = null;
            for (var i = 0; i < listed.Count; i++)
            {
                var tool = listed[i];
                var keep = true;
                if (tools.TryGetPrimitive(tool.Name, out var primitive) && primitive is CairnAffordanceTool { CallerPolicy: { } policy })
                {
                    decisions ??= new(StringComparer.Ordinal);
                    if (!decisions.TryGetValue(policy, out var authorized))
                    {
                        authorized = await AuthorizedAsync(context.User, policy, context.Services, cancellationToken).ConfigureAwait(false);
                        decisions[policy] = authorized;
                    }

                    keep = authorized;
                }

                if (!keep)
                {
                    // First removal: materialize the kept prefix so untouched lists stay allocation-free.
                    kept ??= [.. listed.Take(i)];
                }
                else
                {
                    kept?.Add(tool);
                }
            }

            if (kept is not null)
            {
                result.Tools = kept;
            }

            return result;
        };

    // Mirrors Cairn's request-time authorizer: no way to evaluate means not authorized (links are gated off,
    // not exposed), and a missing IAuthorizationService is a configuration error worth surfacing.
    private static async ValueTask<bool> AuthorizedAsync(ClaimsPrincipal? user, string policy, IServiceProvider? services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (services is null)
        {
            return false;
        }

        var authorization = services.GetService<IAuthorizationService>()
            ?? throw new InvalidOperationException("An affordance declares RequireAuthorization but IAuthorizationService is not registered. Call services.AddAuthorization().");

        user ??= new ClaimsPrincipal(new ClaimsIdentity());
        if (policy.Length == 0)
        {
            var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
            var defaultPolicy = await provider.GetDefaultPolicyAsync().ConfigureAwait(false);
            return (await authorization.AuthorizeAsync(user, resource: null, defaultPolicy).ConfigureAwait(false)).Succeeded;
        }

        return (await authorization.AuthorizeAsync(user, resource: null, policy).ConfigureAwait(false)).Succeeded;
    }
}
