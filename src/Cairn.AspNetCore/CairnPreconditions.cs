using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore;

/// <summary>
/// Evaluates a write request's <c>If-Match</c> precondition against the resource's current entity tag — the
/// server half of the optimistic-concurrency round trip <c>CairnClient</c> initiates with its <c>ifMatch</c>
/// parameter.
/// </summary>
public static class CairnPreconditions
{
    /// <summary>
    /// Checks the request's <c>If-Match</c> header against <paramref name="currentETag"/> (the tag of the
    /// resource's current state, loaded by the handler). Returns <see langword="null"/> when the precondition
    /// passes; otherwise a <c>412 Precondition Failed</c> problem result to return — or
    /// <c>428 Precondition Required</c> when the header is absent and <paramref name="requireIfMatch"/> is set.
    /// </summary>
    /// <param name="request">The current request.</param>
    /// <param name="currentETag">
    /// The entity tag of the resource's current state — the same value the read endpoint emits (quoted
    /// automatically unless already a valid entity tag). Per RFC 9110 §13.1.1 comparison is strong, so a weak
    /// tag (<c>W/"..."</c>) never matches.
    /// </param>
    /// <param name="requireIfMatch">Whether a missing <c>If-Match</c> header fails with <c>428 Precondition Required</c> (lost-update protection).</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="currentETag"/> is null or empty.</exception>
    public static IResult? Evaluate(HttpRequest request, string currentETag, bool requireIfMatch = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(currentETag);

        var header = request.Headers.IfMatch;
        if (header.Count == 0)
        {
            return requireIfMatch
                ? TypedResults.Problem(
                    statusCode: StatusCodes.Status428PreconditionRequired,
                    title: "Precondition Required",
                    detail: "This operation requires an If-Match header carrying the resource's current ETag.")
                : null;
        }

        if (EntityTagHeaderValue.TryParseList(header, out var candidates))
        {
            var current = CairnETagExtensions.Normalize(currentETag);
            foreach (var candidate in candidates)
            {
                if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(current, useStrongComparison: true))
                {
                    return null;
                }
            }
        }

        return TypedResults.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            title: "Precondition Failed",
            detail: "The resource has changed since it was read; refresh it and retry with its current ETag.");
    }
}
