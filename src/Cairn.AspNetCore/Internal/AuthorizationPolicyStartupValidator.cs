using Cairn.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Fails fast at startup when a link configuration references an authorization policy the host never
/// registered. Every policy name is known once the configs are compiled, so a typo should surface as a clear
/// startup error — not as a 500 on the first request that happens to build the gated link.
/// </summary>
internal sealed class AuthorizationPolicyStartupValidator(IServiceProvider services, CairnOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A dynamic policy provider may materialize policies only after boot (per-tenant stores, databases);
        // validating against it at startup would reject policies that exist at request time.
        if (!options.ValidateAuthorizationPolicies)
        {
            return;
        }

        // policy name -> the resource types whose configs reference it (for the error message).
        Dictionary<string, List<string>>? referenced = null;
        foreach (var (resourceType, config) in options.Registry.Snapshot)
        {
            if (config is not IPolicyReportingConfig reporting)
            {
                continue;
            }

            foreach (var policy in reporting.Policies)
            {
                referenced ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!referenced.TryGetValue(policy, out var users))
                {
                    referenced[policy] = users = [];
                }

                users.Add(resourceType.Name);
            }
        }

        if (referenced is null)
        {
            return;
        }

        // Without a policy provider, authorization isn't wired up at all; the request-time error for that
        // case already tells the user to call AddAuthorization(), so don't duplicate it here.
        if (services.GetService<IAuthorizationPolicyProvider>() is not { } provider)
        {
            return;
        }

        // A replaced IAuthorizationService may resolve policy names by its own rules (never consulting the
        // policy provider), so only the default service's resolution path can be validated ahead of time.
        if (services.GetService<IAuthorizationService>() is not DefaultAuthorizationService)
        {
            return;
        }

        List<string>? unknown = null;
        foreach (var (policy, users) in referenced)
        {
            // A provider that resolves policies from external state may throw before that state is reachable
            // (connection strings, tenant context). That is an inconclusive lookup, not a missing policy —
            // don't fail the host over it, and don't let the exception itself abort startup.
            AuthorizationPolicy? resolved;
            try
            {
                resolved = await provider.GetPolicyAsync(policy).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger("Cairn.AspNetCore").LogWarning(
                    ex,
                    "Cairn: startup validation of authorization policy '{Policy}' was skipped because IAuthorizationPolicyProvider.GetPolicyAsync threw. " +
                    "If the provider resolves policies dynamically after boot, disable this check with CairnOptions.ValidateAuthorizationPolicies = false.",
                    policy);
                continue;
            }

            if (resolved is null)
            {
                (unknown ??= []).Add($"'{policy}' (required by the link configuration for {string.Join(", ", users)})");
            }
        }

        if (unknown is not null)
        {
            throw new InvalidOperationException(
                "Cairn: link configurations require authorization policies that are not registered: "
                + string.Join("; ", unknown)
                + ". Register each policy with services.AddAuthorization(options => options.AddPolicy(...)), or fix the name passed to RequireAuthorization(...)."
                + " If policies are provided dynamically at request time, disable this startup check with CairnOptions.ValidateAuthorizationPolicies = false.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
