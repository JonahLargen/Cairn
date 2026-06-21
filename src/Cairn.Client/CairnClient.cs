using System.Net.Http.Json;
using System.Text.Json;

namespace Cairn.Client;

/// <summary>A typed client for consuming Cairn hypermedia APIs.</summary>
public sealed class CairnClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly Func<Uri, bool>? _allowLink;

    /// <summary>Creates a client over the given <see cref="HttpClient"/>.</summary>
    /// <param name="http">The underlying HTTP client (its <see cref="HttpClient.BaseAddress"/> resolves relative URLs).</param>
    /// <param name="jsonOptions">Serialization options (defaults to web/camelCase).</param>
    /// <param name="allowLink">
    /// An optional policy invoked with the absolute target of a followed link or invoked affordance; return
    /// <see langword="false"/> to reject it. Use it to restrict navigation to trusted hosts (e.g. the base
    /// address authority). When <see langword="null"/>, any server-supplied link is followed.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> is null.</exception>
    public CairnClient(HttpClient http, JsonSerializerOptions? jsonOptions = null, Func<Uri, bool>? allowLink = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _json = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _allowLink = allowLink;
    }

    /// <summary>Gets a resource and its hypermedia from <paramref name="url"/>. Does not throw on an HTTP error status.</summary>
    public async Task<ClientResult<T>> GetAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Follows a link to another resource. Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="link"/> is a URI template (<see cref="Link.Templated"/>).</exception>
    /// <exception cref="InvalidOperationException">The link target is rejected by the configured link policy.</exception>
    public async Task<ClientResult<T>> FollowAsync<T>(Link link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Templated)
        {
            throw new NotSupportedException($"The '{link.Relation}' link is a URI template; expanding templated links is not supported.");
        }

        using var response = await _http.GetAsync(Authorize(link.Href), cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Invokes an affordance, optionally with a request body. Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public async Task<ClientResult> InvokeAsync(Affordance affordance, object? body = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(affordance, body, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return ClientResult.Success((int)response.StatusCode);
        }

        return ClientResult.Failure((int)response.StatusCode, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Invokes an affordance and reads its returned resource. Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public async Task<ClientResult<TResult>> InvokeAsync<TResult>(Affordance affordance, object? body = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(affordance, body, cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync<TResult>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Affordance affordance, object? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(affordance);
        using var request = new HttpRequestMessage(new HttpMethod(affordance.Method), Authorize(affordance.Href));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _json);
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Uri Authorize(string href)
    {
        var uri = _http.BaseAddress is { } baseAddress ? new Uri(baseAddress, href) : new Uri(href, UriKind.RelativeOrAbsolute);

        if (_allowLink is not null && uri.IsAbsoluteUri && !_allowLink(uri))
        {
            throw new InvalidOperationException($"The link target '{uri}' is not permitted by the configured link policy.");
        }

        return uri;
    }

    private async Task<ClientResult<T>> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return ClientResult<T>.Success(status, await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false));
        }

        return ClientResult<T>.Failure(status, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
    }

    // Parse the body once: a single JsonDocument binds the typed value and the hypermedia.
    private async Task<Resource<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var value = document.RootElement.Deserialize<T>(_json);
        var (links, affordances) = HypermediaParser.Parse(document.RootElement);
        return new Resource<T>(this, value, links, affordances);
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ProblemReader.ReadFrom(body, (int)response.StatusCode, response.ReasonPhrase);
    }
}
