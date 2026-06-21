using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Internal;

/// <summary>Authorizes policy-gated links against the current caller using <see cref="IAuthorizationService"/>.</summary>
internal sealed class AuthorizationPolicyLinkAuthorizer(IHttpContextAccessor accessor) : ILinkAuthorizer
{
    public async ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default)
    {
        var http = accessor.HttpContext;
        if (http is null)
        {
            return false;
        }

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
