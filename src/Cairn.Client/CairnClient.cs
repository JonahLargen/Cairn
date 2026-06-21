using System.Net.Http.Json;
using System.Text.Json;

namespace Cairn.Client;

/// <summary>A typed client for consuming Cairn hypermedia APIs.</summary>
public sealed class CairnClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    /// <summary>Creates a client over the given <see cref="HttpClient"/>.</summary>
    /// <param name="http">The underlying HTTP client (its <see cref="HttpClient.BaseAddress"/> resolves relative URLs).</param>
    /// <param name="jsonOptions">Serialization options (defaults to web/camelCase).</param>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> is null.</exception>
    public CairnClient(HttpClient http, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _json = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    /// <summary>Gets a resource and its hypermedia from <paramref name="url"/>.</summary>
    public async Task<Resource<T>> GetAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Follows a link to another resource.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> is null.</exception>
    public async Task<Resource<T>> FollowAsync<T>(Link link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        using var response = await _http.GetAsync(link.Href, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Invokes an affordance (using its method and href), optionally with a request body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> is null.</exception>
    public async Task<HttpResponseMessage> InvokeAsync(Affordance affordance, object? body = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affordance);
        using var request = new HttpRequestMessage(new HttpMethod(affordance.Method), affordance.Href);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _json);
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Resource<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<T>(json, _json);
        var (links, affordances) = HypermediaParser.Parse(json);
        return new Resource<T>(this, value, links, affordances);
    }
}
