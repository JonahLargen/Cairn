using System.Net;

namespace Cairn.Client;

/// <summary>
/// Follows redirects itself so the link policy is enforced on every hop — a handler-level auto-redirect
/// would silently step around the policy after the first, policy-approved request. Wired by
/// <c>AddCairnClient</c> (with auto-redirect disabled on the primary handler) when a policy is configured.
/// </summary>
internal sealed class LinkPolicyRedirectHandler(Func<Uri, bool> allowLink) : DelegatingHandler
{
    private const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = request.RequestUri;
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureRedirectsAreVisible(sent, response);

        for (var hops = 0; hops < MaxRedirects && IsRedirect(response.StatusCode) && response.Headers.Location is { } location; hops++)
        {
            var origin = request.RequestUri!;
            var target = location.IsAbsoluteUri ? location : new Uri(origin, location);
            if (!allowLink(target))
            {
                response.Dispose();
                throw new InvalidOperationException($"The redirect target '{target}' is not permitted by the configured link policy.");
            }

            var method = NextMethod(response.StatusCode, request.Method);

            // A body-preserving redirect (307/308) can't be honored: the original content stream may already
            // be consumed. Surface the 3xx to the caller instead of re-sending a broken request.
            if (request.Content is not null && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                return response;
            }

            var next = new HttpRequestMessage(method, target);
            foreach (var header in request.Headers)
            {
                // Credentials never travel to another origin the original request didn't target.
                if (IsCredentialHeader(header.Key) && !SameOrigin(origin, target))
                {
                    continue;
                }

                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Dispose();
            request = next;
            response = await base.SendAsync(next, cancellationToken).ConfigureAwait(false);
            EnsureRedirectsAreVisible(target, response);
        }

        return response;
    }

    // Authorization, Cookie, and Proxy-Authorization all carry credentials; forwarding any of them to a
    // different origin would leak them to a host the caller never targeted.
    private static bool IsCredentialHeader(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    // If the response came back from a different resource than was sent, an inner handler followed a redirect
    // itself — hops this policy never saw. That is a misconfiguration (a primary handler with
    // AllowAutoRedirect left on); fail loudly instead of silently skipping enforcement. Compare only the
    // scheme/host/path, not the query: an inner handler that merely rewrites the request (e.g. appends an
    // API-key query parameter) is not a redirect to another resource and must not trip this guard.
    private static void EnsureRedirectsAreVisible(Uri? sent, HttpResponseMessage response)
    {
        if (sent is null || response.RequestMessage?.RequestUri is not { } final)
        {
            return;
        }

        if (!string.Equals(WithoutQuery(sent), WithoutQuery(final), StringComparison.Ordinal))
        {
            response.Dispose();
            throw new InvalidOperationException(
                $"The response for '{sent}' arrived from '{final}': an inner handler followed a redirect the link policy could not inspect. Disable AllowAutoRedirect on the primary HTTP handler.");
        }
    }

    // The URI up to but not including the query (and fragment): scheme, authority, and path.
    private static string WithoutQuery(Uri uri)
    {
        if (uri.IsAbsoluteUri)
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        var text = uri.OriginalString;
        var query = text.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? text : text[..query];
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    // 303 always re-fetches with GET; 301/302 conventionally rewrite POST to GET; 307/308 preserve the method.
    private static HttpMethod NextMethod(HttpStatusCode status, HttpMethod original)
        => status == HttpStatusCode.SeeOther
            || (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && original == HttpMethod.Post)
            ? HttpMethod.Get
            : original;

    private static bool SameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;
}
