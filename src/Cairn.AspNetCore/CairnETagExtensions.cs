using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for emitting and honoring entity tags on endpoints.</summary>
public static class CairnETagExtensions
{
    /// <summary>
    /// Emits an <c>ETag</c> derived from the response value on this endpoint's (or route group's) responses,
    /// and answers a conditional <c>GET</c>/<c>HEAD</c> whose <c>If-None-Match</c> matches with
    /// <c>304 Not Modified</c> — the server half of the round trip <c>CairnClient</c> initiates with its
    /// <c>ifNoneMatch</c> parameter.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint or route group builder type.</typeparam>
    /// <typeparam name="T">The response value type the tag is derived from.</typeparam>
    /// <param name="builder">The endpoint or route group builder.</param>
    /// <param name="etag">
    /// Derives the entity tag from the response value — typically a version or row-version (e.g.
    /// <c>o =&gt; o.Version.ToString()</c>). The value is quoted automatically unless it is already a valid
    /// entity tag (<c>"v1"</c> or <c>W/"v1"</c>).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="etag"/> is <see langword="null"/>.</exception>
    public static TBuilder WithETag<TBuilder, T>(this TBuilder builder, Func<T, string> etag)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(etag);

        // Leave a marker in the endpoint metadata so the OpenAPI integrations can document the ETag response
        // header and the 304 response; the tag itself is derived per response by the filter below.
        builder.Add(endpoint => endpoint.Metadata.Add(ETagMetadata.Instance));

        return builder.AddEndpointFilterFactory((_, next) => async invocation =>
        {
            var result = await next(invocation);
            if (FindValue(result) is not T typed || etag(typed) is not { Length: > 0 } raw)
            {
                return result;
            }

            var tag = Normalize(raw);
            var http = invocation.HttpContext;
            http.Response.Headers.ETag = tag.ToString();

            // RFC 9110 §13.1.2: If-None-Match uses weak comparison, and applies to GET/HEAD reads here
            // (unsafe methods are the handler's job via CairnPreconditions, which sees the *stored* state).
            if ((HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                && IfNoneMatchMatches(http.Request, tag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return result;
        });
    }

    /// <summary>The response value carried by <paramref name="result"/>: unwraps result unions and value-results, or the bare value itself.</summary>
    private static object? FindValue(object? result)
    {
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        return result switch
        {
            IValueHttpResult { Value: { } value } => value,
            IResult => null,
            _ => result,
        };
    }

    internal static EntityTagHeaderValue Normalize(string raw)
        => EntityTagHeaderValue.TryParse(raw, out var parsed) ? parsed : new EntityTagHeaderValue(Quote(raw));

    // An entity tag only allows etagc characters (RFC 9110 §8.8.3: 0x21, 0x23-0x7E); a selector value carrying
    // a quote, control character, or non-ASCII text would make the EntityTagHeaderValue constructor throw a
    // FormatException at request time. Percent-encode the offending bytes instead ('%' too, so distinct raw
    // values stay distinct) — the tag is an opaque validator, so any stable, header-safe spelling is correct.
    private static string Quote(string raw)
    {
        var builder = new System.Text.StringBuilder(raw.Length + 2).Append('"');
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(raw))
        {
            if (b != (byte)'%' && b is 0x21 or (>= 0x23 and <= 0x7E))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.Append('"').ToString();
    }

    private static bool IfNoneMatchMatches(HttpRequest request, EntityTagHeaderValue current)
    {
        var header = request.Headers.IfNoneMatch;
        if (header.Count == 0 || !EntityTagHeaderValue.TryParseList(header, out var candidates))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(current, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }
}
