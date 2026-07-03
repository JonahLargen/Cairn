using Cairn.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            if (await provider.GetPolicyAsync(policy).ConfigureAwait(false) is null)
            {
                (unknown ??= []).Add($"'{policy}' (required by the link configuration for {string.Join(", ", users)})");
            }
        }

        if (unknown is not null)
        {
            throw new InvalidOperationException(
                "Cairn: link configurations require authorization policies that are not registered: "
                + string.Join("; ", unknown)
                + ". Register each policy with services.AddAuthorization(options => options.AddPolicy(...)), or fix the name passed to RequireAuthorization(...).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
