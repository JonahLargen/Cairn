using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cairn.Client;

/// <summary>A typed client for consuming Cairn hypermedia APIs.</summary>
public sealed class CairnClient
{
    // A single cached instance: constructing options per client would discard System.Text.Json's
    // per-options metadata cache and re-generate it for every client.
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly Func<Uri, bool>? _allowLink;

    /// <summary>Creates a client over the given <see cref="HttpClient"/>.</summary>
    /// <param name="http">The underlying HTTP client (its <see cref="HttpClient.BaseAddress"/> resolves relative URLs).</param>
    /// <param name="jsonOptions">Serialization options (defaults to web/camelCase).</param>
    /// <param name="allowLink">
    /// An optional policy invoked with the absolute target of a followed link or invoked affordance; return
    /// <see langword="false"/> to reject it. Use it to restrict navigation to trusted hosts (e.g. the base
    /// address authority). When <see langword="null"/>, any server-supplied link is followed. Note that a
    /// default <see cref="HttpClient"/> follows redirects inside its handler, where this policy cannot see the
    /// hops — register via <c>AddCairnClient</c> (which enforces the policy on every redirect) or disable
    /// <c>HttpClientHandler.AllowAutoRedirect</c>.
    /// </param>
    /// <remarks>
    /// Unless <paramref name="http"/> already declares default <c>Accept</c> headers, each request asks for
    /// the hypermedia the client can parse: <c>application/prs.hal-forms+json</c>, then
    /// <c>application/hal+json</c>, then <c>application/json</c>. The injected client's
    /// <see cref="HttpClient.DefaultRequestHeaders"/> are never modified.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="http"/> is null.</exception>
    public CairnClient(HttpClient http, JsonSerializerOptions? jsonOptions = null, Func<Uri, bool>? allowLink = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _json = jsonOptions ?? DefaultJsonOptions;
        _allowLink = allowLink;
    }

    /// <summary>
    /// Gets a resource and its hypermedia from <paramref name="url"/>. Pass <paramref name="ifNoneMatch"/> (an ETag) for a
    /// conditional GET — a <c>304</c> response surfaces as <see cref="ClientResult{T}.IsNotModified"/>. Does not throw on an HTTP error status.
    /// </summary>
    public async Task<ClientResult<T>> GetAsync<T>(string url, string? ifNoneMatch = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Follows a link to another resource. Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="link"/> is a URI template (<see cref="Link.Templated"/>); supply variables to expand it.</exception>
    /// <exception cref="InvalidOperationException">The link target is rejected by the configured link policy.</exception>
    public Task<ClientResult<T>> FollowAsync<T>(Link link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Templated)
        {
            throw new NotSupportedException($"The '{link.Relation}' link is a URI template; supply variables to expand it.");
        }

        return FollowResolvedAsync<T>(link.Href, cancellationToken);
    }

    /// <summary>Follows a link, expanding it as an RFC 6570 URI template with <paramref name="variables"/> (an anonymous object or dictionary). Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="link"/> is not templated but <paramref name="variables"/> were supplied.</exception>
    /// <exception cref="InvalidOperationException">The link target is rejected by the configured link policy.</exception>
    public Task<ClientResult<T>> FollowAsync<T>(Link link, object? variables, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        return FollowResolvedAsync<T>(ResolveHref(link, variables), cancellationToken);
    }

    private async Task<ClientResult<T>> FollowResolvedAsync<T>(string href, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, Authorize(href));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a collection from <paramref name="url"/>, with each item as a navigable resource. <paramref name="itemsProperty"/>
    /// names the array property on an envelope (default <c>items</c>); a bare JSON array is read directly. Does not throw on an HTTP error status.
    /// </summary>
    public async Task<CollectionResult<TItem>> GetCollectionAsync<TItem>(string url, string itemsProperty = "items", CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadCollectionResultAsync<TItem>(response, itemsProperty, cancellationToken).ConfigureAwait(false);
    }

    // A templated pagination link (e.g. a "next" carrying "{?page}") expands with the variables; with none,
    // every unresolved expression collapses per RFC 6570, so a templated next/prev stays followable.
    internal async Task<CollectionResult<TItem>> FollowCollectionAsync<TItem>(Link link, object? variables, string itemsProperty, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, Authorize(ResolveHref(link, variables)));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadCollectionResultAsync<TItem>(response, itemsProperty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Invokes an affordance, optionally with a request body and an <paramref name="ifMatch"/> ETag (optimistic concurrency). Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public async Task<ClientResult> InvokeAsync(Affordance affordance, object? body = null, string? ifMatch = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(affordance, body, ifMatch, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
        {
            return ClientResult.Success((int)response.StatusCode);
        }

        return ClientResult.Failure((int)response.StatusCode, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Invokes an affordance and reads its returned resource, optionally with an <paramref name="ifMatch"/> ETag. Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public async Task<ClientResult<TResult>> InvokeAsync<TResult>(Affordance affordance, object? body = null, string? ifMatch = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(affordance, body, ifMatch, cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync<TResult>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits a HAL-FORMS affordance: validates <paramref name="values"/> against <paramref name="fields"/>
    /// (required, read-only, regex, length, range, options) before anything is sent, then sends them with the
    /// affordance's method and declared content type. Does not throw on an HTTP error status.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> or <paramref name="fields"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> fail client-side validation against <paramref name="fields"/>.</exception>
    /// <exception cref="NotSupportedException">The affordance declares a content type the client cannot encode.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public Task<ClientResult> SubmitAsync(Affordance affordance, IReadOnlyList<AffordanceField> fields, object? values = null, string? ifMatch = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affordance);
        ArgumentNullException.ThrowIfNull(fields);
        ValidateSubmission(fields, values);
        return InvokeAsync(affordance, values, ifMatch, cancellationToken);
    }

    /// <summary>
    /// Submits a HAL-FORMS affordance and reads its returned resource: validates <paramref name="values"/>
    /// against <paramref name="fields"/> before anything is sent, then sends them with the affordance's method
    /// and declared content type. Does not throw on an HTTP error status.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> or <paramref name="fields"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> fail client-side validation against <paramref name="fields"/>.</exception>
    /// <exception cref="NotSupportedException">The affordance declares a content type the client cannot encode.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public Task<ClientResult<TResult>> SubmitAsync<TResult>(Affordance affordance, IReadOnlyList<AffordanceField> fields, object? values = null, string? ifMatch = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affordance);
        ArgumentNullException.ThrowIfNull(fields);
        ValidateSubmission(fields, values);
        return InvokeAsync<TResult>(affordance, values, ifMatch, cancellationToken);
    }

    // Client-side HAL-FORMS validation: reject a bad submission before it leaves the process, with the
    // field-level reasons a server would only report back as an error response.
    private void ValidateSubmission(IReadOnlyList<AffordanceField> fields, object? values)
    {
        var element = values is null ? default : JsonSerializer.SerializeToElement(values, _json);
        if (values is not null && element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A submission must serialize to a JSON object of field values.", nameof(values));
        }

        List<string>? errors = null;
        foreach (var field in fields)
        {
            JsonElement value = default;
            var present = element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(field.Name, out value)
                && value.ValueKind is not JsonValueKind.Null;

            if (!present)
            {
                if (field.Required)
                {
                    (errors ??= []).Add($"'{field.Name}' is required.");
                }

                continue;
            }

            if (field.ReadOnly)
            {
                (errors ??= []).Add($"'{field.Name}' is read-only and cannot be submitted.");
                continue;
            }

            ValidateValue(field, value, ref errors);
        }

        if (errors is not null)
        {
            throw new ArgumentException($"The submission is invalid: {string.Join(" ", errors)}", nameof(values));
        }
    }

    private static void ValidateValue(AffordanceField field, JsonElement value, ref List<string>? errors)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (field.Required && text.Length == 0)
            {
                (errors ??= []).Add($"'{field.Name}' is required.");
            }

            if (field.MaxLength is { } maxLength && text.Length > maxLength)
            {
                (errors ??= []).Add($"'{field.Name}' must be at most {maxLength} characters.");
            }

            if (field.Regex is { } pattern && !MatchesPattern(text, pattern))
            {
                (errors ??= []).Add($"'{field.Name}' must match the pattern '{pattern}'.");
            }
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            if (field.Min is { } min && number < min)
            {
                (errors ??= []).Add($"'{field.Name}' must be at least {min}.");
            }

            if (field.Max is { } max && number > max)
            {
                (errors ??= []).Add($"'{field.Name}' must be at most {max}.");
            }
        }

        if (field.Options.Count > 0)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
            if (!field.Options.Contains(text, StringComparer.Ordinal))
            {
                (errors ??= []).Add($"'{field.Name}' must be one of: {string.Join(", ", field.Options)}.");
            }
        }
    }

    // HAL-FORMS regex follows HTML5 pattern semantics: it must match the whole value. A pattern the runtime
    // cannot evaluate (invalid, or pathological enough to time out) never invalidates the value.
    private static bool MatchesPattern(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, $"^(?:{pattern})$", RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Affordance affordance, object? body, string? ifMatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(affordance);
        using var request = CreateRequest(new HttpMethod(affordance.Method), Authorize(affordance.Href));
        if (body is not null)
        {
            request.Content = CreateContent(body, affordance.ContentType);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Honor the affordance's declared contentType (HAL-FORMS): JSON media types serialize the body as JSON
    // under that label; form encoding flattens the body's top-level scalars. Anything else must be handed to
    // us pre-encoded, so fail loudly rather than mislabel a JSON payload.
    private HttpContent CreateContent(object body, string? contentType)
    {
        if (contentType is null)
        {
            return JsonContent.Create(body, options: _json);
        }

        // Parse first, then compare the parsed media type: a parameterized declaration such as
        // "application/json; charset=utf-8" is still JSON and keeps its parameters on the request.
        if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            && mediaType.MediaType is { } label)
        {
            if (string.Equals(label, "application/json", StringComparison.OrdinalIgnoreCase)
                || label.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            {
                return JsonContent.Create(body, mediaType, _json);
            }

            if (string.Equals(label, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                return new FormUrlEncodedContent(FormPairs(body));
            }
        }

        throw new NotSupportedException(
            $"The affordance's content type '{contentType}' is not supported. Use application/json, a +json media type, or application/x-www-form-urlencoded.");
    }

    private IEnumerable<KeyValuePair<string, string>> FormPairs(object body)
    {
        if (body is IEnumerable<KeyValuePair<string, string>> pairs)
        {
            return pairs;
        }

        var element = JsonSerializer.SerializeToElement(body, _json);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new NotSupportedException("A form-encoded affordance body must be an object (or key/value pairs).");
        }

        var flattened = new List<KeyValuePair<string, string>>();
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    break;
                case JsonValueKind.String:
                    flattened.Add(new(property.Name, property.Value.GetString()!));
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    flattened.Add(new(property.Name, property.Value.GetRawText()));
                    break;
                default:
                    throw new NotSupportedException($"The '{property.Name}' value is a nested {property.Value.ValueKind}; it cannot be form-encoded.");
            }
        }

        return flattened;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url) => WithAccept(new HttpRequestMessage(method, url));

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri) => WithAccept(new HttpRequestMessage(method, uri));

    // Ask per request for the hypermedia the client can parse. The injected HttpClient's
    // DefaultRequestHeaders are never mutated: the client may be shared with other consumers, and default
    // headers are not safe to change while requests are in flight. Caller-declared defaults still win.
    private HttpRequestMessage WithAccept(HttpRequestMessage request)
    {
        if (_http.DefaultRequestHeaders.Accept.Count == 0)
        {
            request.Headers.Accept.ParseAdd("application/prs.hal-forms+json");
            request.Headers.Accept.ParseAdd("application/hal+json; q=0.9");
            request.Headers.Accept.ParseAdd("application/json; q=0.8");
        }

        return request;
    }

    // Expands a templated href with the variables; a non-templated link accepts none — silently dropping
    // them would hide a caller bug (the request would go to the unmodified href).
    private static string ResolveHref(Link link, object? variables)
    {
        if (link.Templated)
        {
            return UriTemplate.Expand(link.Href, variables);
        }

        if (variables is not null)
        {
            throw new ArgumentException($"The '{link.Relation}' link is not templated; it does not accept variables.", nameof(variables));
        }

        return link.Href;
    }

    private Uri Authorize(string href)
    {
        var uri = _http.BaseAddress is { } baseAddress ? new Uri(baseAddress, href) : new Uri(href, UriKind.RelativeOrAbsolute);

        if (_allowLink is not null)
        {
            // Fail closed: a configured policy must not be silently skipped just because the target stayed
            // relative (no BaseAddress), which would let a relative link through unchecked.
            if (!uri.IsAbsoluteUri)
            {
                throw new InvalidOperationException($"The link target '{href}' could not be resolved to an absolute URI, so the link policy cannot be enforced — set HttpClient.BaseAddress.");
            }

            if (!_allowLink(uri))
            {
                throw new InvalidOperationException($"The link target '{uri}' is not permitted by the configured link policy.");
            }
        }

        return uri;
    }

    private async Task<ClientResult<T>> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        // 304 Not Modified is a distinct, non-error outcome of a conditional request: the caller's cached
        // representation is still fresh and no body was sent, so there is nothing to parse and nothing to
        // throw about. Surface it as a bodiless success the caller can distinguish via IsNotModified.
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return ClientResult<T>.Success(status, EmptyResource<T>(response.Headers.ETag?.ToString()));
        }

        if (!response.IsSuccessStatusCode)
        {
            return ClientResult<T>.Failure(status, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var etag = response.Headers.ETag?.ToString();
        if (IsBlank(bytes))
        {
            return ClientResult<T>.Success(status, EmptyResource<T>(etag));
        }

        try
        {
            // Parse the body once: a single JsonDocument binds the typed value and the hypermedia.
            using var document = JsonDocument.Parse(bytes);
            return ClientResult<T>.Success(status, BuildResource<T>(document.RootElement, etag));
        }
        catch (JsonException exception)
        {
            return ClientResult<T>.Failure(status, ParseFailure(response, bytes, exception));
        }
    }

    private Resource<T> EmptyResource<T>(string? etag)
        => new(this, default, Empty<IReadOnlyList<Link>>(), Empty<Affordance>(), Empty<IReadOnlyList<AffordanceField>>(), etag);

    private async Task<CollectionResult<TItem>> ReadCollectionResultAsync<TItem>(HttpResponseMessage response, string itemsProperty, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return CollectionResult<TItem>.Success(status, new CollectionResource<TItem>(this, [], Empty<IReadOnlyList<Link>>(), Empty<Affordance>()));
        }

        if (!response.IsSuccessStatusCode)
        {
            return CollectionResult<TItem>.Failure(status, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (IsBlank(bytes))
        {
            return CollectionResult<TItem>.Success(status, new CollectionResource<TItem>(this, [], Empty<IReadOnlyList<Link>>(), Empty<Affordance>()));
        }

        try
        {
            return CollectionResult<TItem>.Success(status, ReadCollection<TItem>(bytes, itemsProperty));
        }
        catch (JsonException exception)
        {
            return CollectionResult<TItem>.Failure(status, ParseFailure(response, bytes, exception));
        }
    }

    private CollectionResource<TItem> ReadCollection<TItem>(byte[] bytes, string itemsProperty)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        // A bare array carries no collection-level links; an envelope's links live on its root object.
        var parsed = root.ValueKind == JsonValueKind.Object ? HypermediaParser.Parse(root) : ParsedHypermedia.Empty;
        var (links, affordances) = (parsed.Links, parsed.Affordances);

        var elements = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty(itemsProperty, out var array) && array.ValueKind == JsonValueKind.Array ? array
            : default;

        var items = new List<Resource<TItem>>();
        if (elements.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in elements.EnumerateArray())
            {
                items.Add(BuildResource<TItem>(element));
            }
        }

        return new CollectionResource<TItem>(this, items, links, affordances);
    }

    // A success body that isn't valid JSON must surface through the result contract (a Problem the caller
    // can inspect, or a CairnClientException from EnsureSuccess) — never as a raw JsonException.
    private static Problem ParseFailure(HttpResponseMessage response, byte[] bytes, JsonException exception)
        => new()
        {
            Title = "The response body is not valid JSON.",
            Status = (int)response.StatusCode,
            Detail = $"The '{response.Content.Headers.ContentType?.ToString() ?? "unknown"}' body could not be parsed: {exception.Message} The body starts with: {Snippet(bytes)}",
        };

    private static string Snippet(byte[] bytes)
    {
        const int MaxChars = 120;
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxChars * 4)).ReplaceLineEndings(" ");
        return text.Length <= MaxChars ? text : text[..MaxChars] + "…";
    }

    private static IReadOnlyDictionary<string, TValue> Empty<TValue>() => new Dictionary<string, TValue>();

    // An empty or whitespace-only body (e.g. 204 No Content, or a 200 with no payload) is not JSON.
    private static bool IsBlank(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }

    // Builds a resource from a parsed element: binds the typed value and its links/affordances/fields/embedded.
    internal Resource<T> BuildResource<T>(JsonElement element, string? etag = null)
    {
        var value = element.Deserialize<T>(_json);
        var parsed = HypermediaParser.Parse(element);
        return new Resource<T>(this, value, parsed.Links, parsed.Affordances, parsed.Fields, etag, parsed.Embedded, parsed.Curies);
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ProblemReader.ReadFrom(body, (int)response.StatusCode, response.ReasonPhrase);
    }
}
