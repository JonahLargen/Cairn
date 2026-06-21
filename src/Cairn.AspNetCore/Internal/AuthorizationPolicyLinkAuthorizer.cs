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
                $"A hypermedia link requires the authorization policy '{policy}', but authorization services are " +
                "not registered. Call IServiceCollection.AddAuthorization() in startup.");

        var result = await authorization.AuthorizeAsync(http.User, policy);
        return result.Succeeded;
    }
}
