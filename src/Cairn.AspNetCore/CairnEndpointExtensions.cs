using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for opting endpoints into Cairn hypermedia.</summary>
public static class CairnEndpointExtensions
{
    /// <summary>
    /// Projects hypermedia links and affordances onto this endpoint's response. The returned value — and
    /// each element of a returned collection — is linked according to its runtime type's configuration.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static RouteHandlerBuilder WithLinks(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            await CairnLinkRecorder.RecordAsync(context.HttpContext, result);
            return result;
        });
    }
}
