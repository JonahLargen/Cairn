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

    // Resource-based decisions are a pure function of (user, resource, policy) for the request, so the same
    // (resource, policy) pair is memoized too — a resource that exposes several links or affordances gated on
    // one policy (an edit link plus an edit action, say) then evaluates it once.
    private const string ResourceCacheKey = "Cairn.ResourcePolicyCache";

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

        var result = await EvaluateAsync(http, policy, resource: null);
        cache[policy] = result;
        return result;
    }

    public async ValueTask<bool> AuthorizeAsync(object? resource, string policy, CancellationToken cancellationToken = default)
    {
        var http = accessor.HttpContext;

        // Nothing resource-specific to evaluate — either no resource (a handler sees a null Resource either way)
        // or no request context to resolve services and cache against — so the decision is the caller-only one.
        // Reuse that path, which brings its per-policy cache and its null-context guard.
        if (resource is null || http is null)
        {
            return await AuthorizeAsync(policy, cancellationToken);
        }

        // Key by reference, not value: two DTOs that compare equal (records do so by default) are still distinct
        // resources to an authorization handler, so one's decision must not stand in for the other's.
        if (http.Items[ResourceCacheKey] is not Dictionary<object, Dictionary<string, bool>> cache)
        {
            cache = new Dictionary<object, Dictionary<string, bool>>(ReferenceEqualityComparer.Instance);
            http.Items[ResourceCacheKey] = cache;
        }

        if (!cache.TryGetValue(resource, out var byPolicy))
        {
            cache[resource] = byPolicy = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        if (byPolicy.TryGetValue(policy, out var cached))
        {
            return cached;
        }

        var result = await EvaluateAsync(http, policy, resource);
        byPolicy[policy] = result;
        return result;
    }

    private static async ValueTask<bool> EvaluateAsync(HttpContext http, string policy, object? resource)
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
                ? await authorization.AuthorizeAsync(http.User, resource, defaultPolicy)
                : http.User?.Identity?.IsAuthenticated == true ? AuthorizationResult.Success() : AuthorizationResult.Failed();
            return defaultResult.Succeeded;
        }

        var result = await authorization.AuthorizeAsync(http.User, resource, policy);
        return result.Succeeded;
    }
}
