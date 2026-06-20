using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for opting endpoints into Cairn hypermedia.</summary>
public static class CairnEndpointExtensions
{
    /// <summary>
    /// Projects hypermedia links and affordances onto responses of type <typeparamref name="T"/> from this endpoint.
    /// </summary>
    /// <typeparam name="T">The response resource type to link.</typeparam>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static RouteHandlerBuilder WithLinks<T>(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            await CairnLinkRecorder.RecordAsync<T>(context.HttpContext, result);
            return result;
        });
    }
}
