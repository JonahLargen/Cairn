using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore;

/// <summary>
/// Evaluates a write request's conditional headers (<c>If-Match</c>, <c>If-None-Match</c>) against the
/// resource's current entity tag — the server half of the optimistic-concurrency round trip
/// <c>CairnClient</c> initiates with its <c>ifMatch</c> parameter.
/// </summary>
public static class CairnPreconditions
{
    /// <summary>
    /// Checks the request's preconditions against <paramref name="currentETag"/> (the tag of the resource's
    /// current state, loaded by the handler; pass <see langword="null"/> when the resource does not exist
    /// yet). Evaluated per RFC 9110 §13: <c>If-Match</c> fails with <c>412</c> unless a listed tag
    /// strong-matches the current one (or <c>*</c> and the resource exists); <c>If-None-Match</c> fails with
    /// <c>412</c> when a listed tag weak-matches (or <c>*</c> and the resource exists) — supporting the
    /// create-only <c>PUT ... If-None-Match: *</c> idiom. Returns <see langword="null"/> when the
    /// preconditions pass; otherwise the problem result to return — or <c>428 Precondition Required</c> when
    /// no conditional header is present and <paramref name="requireIfMatch"/> is set.
    /// </summary>
    /// <param name="request">The current request.</param>
    /// <param name="currentETag">
    /// The entity tag of the resource's current state — the same value the read endpoint emits (quoted
    /// automatically unless already a valid entity tag) — or <see langword="null"/>/empty when the resource
    /// has no current representation (it doesn't exist yet). Per RFC 9110 §13.1.1 <c>If-Match</c> comparison
    /// is strong, so a weak tag (<c>W/"..."</c>) never matches it; <c>If-None-Match</c> uses weak comparison
    /// (§13.1.2).
    /// </param>
    /// <param name="requireIfMatch">Whether a request carrying no conditional header fails with <c>428 Precondition Required</c> (lost-update protection). A creation guarded by <c>If-None-Match: *</c> satisfies the requirement.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static IResult? Evaluate(HttpRequest request, string? currentETag, bool requireIfMatch = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = string.IsNullOrEmpty(currentETag) ? null : CairnETagExtensions.Normalize(currentETag);

        // RFC 9110 §13.2.2: If-Match is evaluated before If-None-Match.
        var ifMatch = request.Headers.IfMatch;
        if (ifMatch.Count > 0)
        {
            if (!Matches(ifMatch, current, useStrongComparison: true))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status412PreconditionFailed,
                    title: "Precondition Failed",
                    detail: "The resource has changed since it was read; refresh it and retry with its current ETag.");
            }
        }

        var ifNoneMatch = request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0)
        {
            // On an unsafe method a matching If-None-Match must fail with 412 (RFC 9110 §13.1.2) — this is
            // what makes `PUT ... If-None-Match: *` a create-only request.
            if (Matches(ifNoneMatch, current, useStrongComparison: false))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status412PreconditionFailed,
                    title: "Precondition Failed",
                    detail: "The If-None-Match precondition failed: the resource already has a current representation.");
            }

            return null;
        }

        if (ifMatch.Count == 0 && requireIfMatch)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "Precondition Required",
                detail: "This operation requires an If-Match header carrying the resource's current ETag.");
        }

        return null;
    }

    // Whether any listed entity tag matches the current one. `*` matches any current representation, so it
    // matches exactly when the resource exists; an unparseable header matches nothing.
    private static bool Matches(Microsoft.Extensions.Primitives.StringValues header, EntityTagHeaderValue? current, bool useStrongComparison)
    {
        if (!EntityTagHeaderValue.TryParseList(header, out var candidates))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any))
            {
                if (current is not null)
                {
                    return true;
                }

                continue;
            }

            if (current is not null && candidate.Compare(current, useStrongComparison))
            {
                return true;
            }
        }

        return false;
    }
}
