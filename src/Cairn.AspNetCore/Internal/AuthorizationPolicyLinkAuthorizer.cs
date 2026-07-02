using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Internal;

/// <summary>Authorizes policy-gated links against the current caller using <see cref="IAuthorizationService"/>.</summary>
internal sealed class AuthorizationPolicyLinkAuthorizer(IHttpContextAccessor accessor) : ILinkAuthorizer
{
    // A policy decision is a pure function of (user, policy) for the lifetime of a request, and building links
    // for a page of N items re-asks the same question N times — memoize per request (link building is
    // sequential, so the plain dictionary is never mutated concurrently).
    private const string CacheKey = "Cairn.PolicyCache";

    public async ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default)
    {
        var http = accessor.HttpContext;
        if (http is null)
        {
            return false;
        }

        if (http.Items[CacheKey] is not Dictionary<string, bool> cache)
        {
            cache = new Dictionary<string, bool>(StringComparer.Ordinal);
            http.Items[CacheKey] = cache;
        }

        if (cache.TryGetValue(policy, out var cached))
        {
            return cached;
        }

        var result = await EvaluateAsync(http, policy);
        cache[policy] = result;
        return result;
    }

    private static async ValueTask<bool> EvaluateAsync(HttpContext http, string policy)
    {
        var authorization = http.RequestServices.GetService<IAuthorizationService>()
            ?? throw new InvalidOperationException(
                "A hypermedia link requires an authorization policy, but authorization services are " +
                "not registered. Call IServiceCollection.AddAuthorization() in startup.");

        // An empty policy name means "the host's default policy" (RequireAuthorization() with no argument).
        if (string.IsNullOrEmpty(policy))
        {
            var provider = http.RequestServices.GetService<IAuthorizationPolicyProvider>();
            var defaultPolicy = provider is null ? null : await provider.GetDefaultPolicyAsync();
            var defaultResult = defaultPolicy is not null
                ? await authorization.AuthorizeAsync(http.User, defaultPolicy)
                : http.User?.Identity?.IsAuthenticated == true ? AuthorizationResult.Success() : AuthorizationResult.Failed();
            return defaultResult.Succeeded;
        }

        var result = await authorization.AuthorizeAsync(http.User, policy);
        return result.Succeeded;
    }
}
