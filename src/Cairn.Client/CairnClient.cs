using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    internal const string TemplateVariablesRequiresUnreferencedCode =
        "URI template variables supplied as an anonymous or POCO object are read via reflection, which trimming may remove. "
        + "Variables supplied as an IReadOnlyDictionary<string, object?> expand without reflection.";

    private const string JsonSuppressionJustification =
        "User payloads (de)serialize through the caller-supplied JsonSerializerOptions. Trimmed and Native AOT applications "
        + "must construct CairnClient with source-generated options (see docs/articles/aot.md); System.Text.Json then resolves "
        + "contracts without reflection and fails with a descriptive exception when a contract is missing.";

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
    /// <para>
    /// Unless <paramref name="http"/> already declares default <c>Accept</c> headers, each request asks for
    /// the hypermedia the client can parse: <c>application/prs.hal-forms+json</c>, then
    /// <c>application/hal+json</c>, then <c>application/json</c>. The injected client's
    /// <see cref="HttpClient.DefaultRequestHeaders"/> are never modified.
    /// </para>
    /// <para>
    /// Response bodies are read headers-first and streamed into the JSON parser rather than buffered twice,
    /// and their size is limited by <see cref="HttpClient.MaxResponseContentBufferSize"/> (an oversized body
    /// throws <see cref="HttpRequestException"/>). The default limit is 2 GB — lower it on
    /// <paramref name="http"/> when talking to servers that are not fully trusted.
    /// </para>
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
    public Task<ClientResult<T>> GetAsync<T>(string url, string? ifNoneMatch = null, CancellationToken cancellationToken = default)
        => TimeboxedAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, url);
            if (ifNoneMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await ReadResultAsync<T>(response, token).ConfigureAwait(false);
        }, cancellationToken);

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
    [RequiresUnreferencedCode(TemplateVariablesRequiresUnreferencedCode)]
    public Task<ClientResult<T>> FollowAsync<T>(Link link, object? variables, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        return FollowResolvedAsync<T>(ResolveHref(link, variables), cancellationToken);
    }

    private Task<ClientResult<T>> FollowResolvedAsync<T>(string href, CancellationToken cancellationToken)
        => TimeboxedAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, Authorize(href));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await ReadResultAsync<T>(response, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Gets a collection from <paramref name="url"/>, with each item as a navigable resource. <paramref name="itemsProperty"/>
    /// names the array property on an envelope (default <c>items</c>); a bare JSON array is read directly. Does not throw on an HTTP error status.
    /// </summary>
    public Task<CollectionResult<TItem>> GetCollectionAsync<TItem>(string url, string itemsProperty = "items", CancellationToken cancellationToken = default)
        => GetCollectionAsync<TItem>(url, itemsProperty, ifNoneMatch: null, cancellationToken);

    /// <summary>
    /// Gets a collection from <paramref name="url"/>, with each item as a navigable resource. <paramref name="itemsProperty"/>
    /// names the array property on an envelope (default <c>items</c>); a bare JSON array is read directly. Pass
    /// <paramref name="ifNoneMatch"/> (an ETag) for a conditional GET — a <c>304</c> response surfaces as
    /// <see cref="CollectionResult{TItem}.IsNotModified"/>. Does not throw on an HTTP error status.
    /// </summary>
    /// <remarks>
    /// This overload is separate from the token-only <see cref="GetCollectionAsync{TItem}(string, string, CancellationToken)"/>
    /// so that adding conditional-GET support stayed binary-compatible: callers compiled against the earlier
    /// signature keep binding to it, and a positional <see cref="CancellationToken"/> still resolves there.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="itemsProperty"/> is <see langword="null"/> — pass the default <c>"items"</c> (or the envelope's array property), never <see langword="null"/>.</exception>
    public Task<CollectionResult<TItem>> GetCollectionAsync<TItem>(string url, string itemsProperty, string? ifNoneMatch, CancellationToken cancellationToken = default)
    {
        // Guard here rather than null-ref later reading the items property (matching CollectionResource.FollowAsync).
        ArgumentNullException.ThrowIfNull(itemsProperty);
        return TimeboxedAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, url);
            if (ifNoneMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await ReadCollectionResultAsync<TItem>(response, itemsProperty, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    // A templated pagination link (e.g. a "next" carrying "{?page}") expands with the variables; with none,
    // every unresolved expression collapses per RFC 6570, so a templated next/prev stays followable.
    internal Task<CollectionResult<TItem>> FollowCollectionAsync<TItem>(Link link, string itemsProperty, CancellationToken cancellationToken)
        => TimeboxedAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, Authorize(ResolveHref(link)));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await ReadCollectionResultAsync<TItem>(response, itemsProperty, token).ConfigureAwait(false);
        }, cancellationToken);

    [RequiresUnreferencedCode(TemplateVariablesRequiresUnreferencedCode)]
    internal Task<CollectionResult<TItem>> FollowCollectionAsync<TItem>(Link link, object? variables, string itemsProperty, CancellationToken cancellationToken)
        => TimeboxedAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, Authorize(ResolveHref(link, variables)));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await ReadCollectionResultAsync<TItem>(response, itemsProperty, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Invokes an affordance, optionally with a request body and an <paramref name="ifMatch"/> ETag (optimistic concurrency). Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public Task<ClientResult> InvokeAsync(Affordance affordance, object? body = null, string? ifMatch = null, CancellationToken cancellationToken = default)
        => TimeboxedAsync(async token =>
        {
            using var response = await SendAsync(affordance, body, ifMatch, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
            {
                return ClientResult.Success((int)response.StatusCode);
            }

            return ClientResult.Failure((int)response.StatusCode, await ReadProblemAsync(response, token).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Invokes an affordance and reads its returned resource, optionally with an <paramref name="ifMatch"/> ETag. Does not throw on an HTTP error status.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affordance"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The affordance target is rejected by the configured link policy.</exception>
    public Task<ClientResult<TResult>> InvokeAsync<TResult>(Affordance affordance, object? body = null, string? ifMatch = null, CancellationToken cancellationToken = default)
        => TimeboxedAsync(async token =>
        {
            using var response = await SendAsync(affordance, body, ifMatch, token).ConfigureAwait(false);
            return await ReadResultAsync<TResult>(response, token).ConfigureAwait(false);
        }, cancellationToken);

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
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = JsonSuppressionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = JsonSuppressionJustification)]
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

            // An empty optional value is "not provided" (mirroring HTML minlength); Required covers the rest.
            if (field.MinLength is { } minLength && text.Length > 0 && text.Length < minLength)
            {
                (errors ??= []).Add($"'{field.Name}' must be at least {minLength} characters.");
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

        // Range checks apply to a numeric value whether it was submitted as a JSON number or as a numeric
        // string (a form field carries "150", not 150) — otherwise "150" would slip past a Max of 100.
        if (TryGetNumber(value, out var number))
        {
            if (field.Min is { } min && number < min)
            {
                (errors ??= []).Add($"'{field.Name}' must be at least {min}.");
            }

            if (field.Max is { } max && number > max)
            {
                (errors ??= []).Add($"'{field.Name}' must be at most {max}.");
            }

            // HAL-FORMS step (HTML5 step semantics): the value must be an integral number of steps from the
            // base — the field's min, or 0. Compared with a small relative tolerance so binary floating-point
            // rounding (0.1 + 0.2) does not flag an otherwise-valid value. A non-finite value (from a "NaN" /
            // "Infinity" string) yields a NaN comparison that is never > the tolerance, so it raises no step
            // error without a separate finiteness guard.
            if (field.Step is { } step && step > 0)
            {
                var steps = (number - (field.Min ?? 0)) / step;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-9 * Math.Max(1, Math.Abs(steps)))
                {
                    (errors ??= []).Add($"'{field.Name}' must be a multiple of {step}{(field.Min is { } b ? $" starting from {b}" : string.Empty)}.");
                }
            }
        }

        if (field.Options.Count > 0)
        {
            // A multi-select field submits an array; each element must be a valid option on its own. Comparing
            // the whole array's raw text to a single scalar option would reject every multi-select submission.
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in value.EnumerateArray())
                {
                    ValidateOption(field, element, ref errors);
                }
            }
            else
            {
                ValidateOption(field, value, ref errors);
            }
        }
    }

    private static void ValidateOption(AffordanceField field, JsonElement value, ref List<string>? errors)
    {
        // Compare the JSON scalar the server would receive: GetRawText keeps a bool as "true"/"false"
        // (the values the server emits in its options), where JsonElement.ToString() yields "True".
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (!field.Options.Any(option => string.Equals(option.Value, text, StringComparison.Ordinal)))
        {
            (errors ??= []).Add($"'{field.Name}' must be one of: {string.Join(", ", field.Options.Select(option => option.Value))}.");
        }
    }

    // A field value as a double, whether encoded as a JSON number or a numeric string. A non-numeric value
    // (or a string that does not parse) is not a range violation — it fails type/required checks elsewhere.
    private static bool TryGetNumber(JsonElement value, out double number)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out number);
            case JsonValueKind.String:
                return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            default:
                number = 0;
                return false;
        }
    }

    // HAL-FORMS regex follows HTML5 pattern semantics — an ECMAScript regex anchored to the whole value.
    // ECMAScript mode makes \d/\w/\s ASCII (not Unicode) and the \A...\z anchors bind to the absolute
    // start/end (a plain $ would also match just before a trailing \n), so values a spec-compliant validator
    // rejects are not silently accepted. A pattern the runtime cannot evaluate (invalid under ECMAScript, or
    // pathological enough to time out) never invalidates the value.
    private static bool MatchesPattern(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, $@"\A(?:{pattern})\z", RegexOptions.ECMAScript | RegexOptions.CultureInvariant, RegexTimeout);
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
            request.Headers.TryAddWithoutValidation("If-Match", StrongIfMatch(ifMatch));
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    // The raw ETag header, preserving a value the typed HttpResponseHeaders.ETag parser rejects (a
    // non-RFC-7232 tag still round-trips as an opaque validator on a later conditional request). Null when the
    // response carries no ETag.
    private static string? ReadETag(HttpResponseMessage response)
    {
        if (response.Headers.NonValidated.TryGetValues("ETag", out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    // RFC 9110 §13.1.1 compares If-Match with the strong function, so a weak validator (W/"...") must not be
    // sent there. Send its strong form instead: a spurious 412 is the safe failure, whereas dropping the
    // header would silently forfeit the caller's lost-update protection.
    private static string StrongIfMatch(string ifMatch)
    {
        var trimmed = ifMatch.TrimStart();
        return trimmed.StartsWith("W/", StringComparison.Ordinal) ? trimmed[2..].TrimStart() : ifMatch;
    }

    // Honor the affordance's declared contentType (HAL-FORMS): JSON media types serialize the body as JSON
    // under that label; form encoding flattens the body's top-level scalars. Anything else must be handed to
    // us pre-encoded, so fail loudly rather than mislabel a JSON payload.
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = JsonSuppressionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = JsonSuppressionJustification)]
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

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = JsonSuppressionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = JsonSuppressionJustification)]
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
    private static string ResolveHref(Link link)
        => link.Templated ? UriTemplate.Expand(link.Href) : link.Href;

    [RequiresUnreferencedCode(TemplateVariablesRequiresUnreferencedCode)]
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
            return ClientResult<T>.Success(status, EmptyResource<T>(ReadETag(response)));
        }

        if (!response.IsSuccessStatusCode)
        {
            return ClientResult<T>.Failure(status, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
        }

        var etag = ReadETag(response);
        var (document, problem) = await ParseBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return ClientResult<T>.Failure(status, problem);
        }

        if (document is null)
        {
            return ClientResult<T>.Success(status, EmptyResource<T>(etag));
        }

        using (document)
        {
            try
            {
                // A single JsonDocument binds the typed value and the hypermedia.
                return ClientResult<T>.Success(status, BuildResource<T>(document.RootElement, etag));
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return ClientResult<T>.Failure(status, BindFailure(typeof(T), response, exception));
            }
        }
    }

    private Resource<T> EmptyResource<T>(string? etag)
        => new(this, default, Empty<IReadOnlyList<Link>>(), Empty<Affordance>(), Empty<IReadOnlyList<AffordanceField>>(), etag);

    private async Task<CollectionResult<TItem>> ReadCollectionResultAsync<TItem>(HttpResponseMessage response, string itemsProperty, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var etag = ReadETag(response);

        // 304 Not Modified answers a conditional collection GET: the caller's cached page is still fresh and
        // no body was sent, so surface a bodiless success (empty items) that preserves the ETag, mirroring the
        // single-resource path.
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return CollectionResult<TItem>.Success(status, new CollectionResource<TItem>(this, [], Empty<IReadOnlyList<Link>>(), Empty<Affordance>(), etag));
        }

        if (!response.IsSuccessStatusCode)
        {
            return CollectionResult<TItem>.Failure(status, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
        }

        var (document, problem) = await ParseBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return CollectionResult<TItem>.Failure(status, problem);
        }

        if (document is null)
        {
            return CollectionResult<TItem>.Success(status, new CollectionResource<TItem>(this, [], Empty<IReadOnlyList<Link>>(), Empty<Affordance>(), etag));
        }

        using (document)
        {
            try
            {
                return CollectionResult<TItem>.Success(status, ReadCollection<TItem>(document.RootElement, itemsProperty, etag));
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return CollectionResult<TItem>.Failure(status, BindFailure(typeof(TItem), response, exception));
            }
        }
    }

    private CollectionResource<TItem> ReadCollection<TItem>(JsonElement root, string itemsProperty, string? etag)
    {
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

        return new CollectionResource<TItem>(this, items, links, affordances, etag);
    }

    // Streams the body straight into the parser — the only whole-body buffer is the JsonDocument's own —
    // while enforcing HttpClient.MaxResponseContentBufferSize, which HttpClient itself no longer applies
    // once responses are read headers-first. A blank body (e.g. 204 No Content, or a 200 with no payload)
    // returns neither a document nor a problem.
    private async Task<(JsonDocument? Document, Problem? Problem)> ParseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = new CappedBodyStream(stream, _http.MaxResponseContentBufferSize);
        try
        {
            return (await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false), null);
        }
        catch (JsonException exception)
        {
            return body.SawContent ? (null, ParseFailure(response, body.Prefix, exception)) : (null, null);
        }
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

    // The body is valid JSON that doesn't deserialize to the requested type — report the binding failure
    // as itself (not as "not valid JSON"), and keep NotSupportedException (an unbindable target type)
    // inside the no-throw result contract.
    private static Problem BindFailure(Type type, HttpResponseMessage response, Exception exception)
        => new()
        {
            Title = $"The response body could not be bound to '{type.Name}'.",
            Status = (int)response.StatusCode,
            Detail = $"The '{response.Content.Headers.ContentType?.ToString() ?? "unknown"}' body is valid JSON, but it could not be deserialized: {exception.Message}",
        };

    private static string Snippet(byte[] bytes)
    {
        const int MaxChars = 120;
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxChars * 4)).ReplaceLineEndings(" ");
        return text.Length <= MaxChars ? text : text[..MaxChars] + "…";
    }

    private static IReadOnlyDictionary<string, TValue> Empty<TValue>() => new Dictionary<string, TValue>();

    // With headers-first responses HttpClient.Timeout covers only the wait for the headers; apply the same
    // budget across the whole exchange (send + body read) so a slowly dripping body cannot hold the caller
    // past the timeout the buffered default used to enforce. When the budget elapses, follow HttpClient's
    // own convention — a TaskCanceledException with a TimeoutException inner — so callers can keep telling
    // a timeout apart from their own cancellation.
    private async Task<TResult> TimeboxedAsync<TResult>(Func<CancellationToken, Task<TResult>> exchange, CancellationToken cancellationToken)
    {
        using var timebox = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            timebox.CancelAfter(_http.Timeout);
        }

        try
        {
            return await exchange(timebox.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timebox.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var message = $"The request was canceled due to the configured HttpClient.Timeout of {_http.Timeout.TotalSeconds} seconds elapsing.";
            throw new TaskCanceledException(message, new TimeoutException(message, exception), timebox.Token);
        }
    }

    // Builds a resource from a parsed element: binds the typed value and its links/affordances/fields/embedded.
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = JsonSuppressionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = JsonSuppressionJustification)]
    internal Resource<T> BuildResource<T>(JsonElement element, string? etag = null)
    {
        var value = element.Deserialize<T>(_json);
        var parsed = HypermediaParser.Parse(element);
        return new Resource<T>(this, value, parsed.Links, parsed.Affordances, parsed.Fields, etag, parsed.Embedded, parsed.Curies);
    }

    // A problem document is small (RFC 9457 members plus a little hypermedia); cap the error-body buffer at
    // 1 MiB rather than the multi-gigabyte success-body limit, so a hostile or misbehaving server's oversized
    // error body degrades to the status-derived problem long before it becomes a large allocation.
    private const int MaxProblemBodyBytes = 1 << 20;

    // Error bodies are buffered by truncation: an oversized error document degrades to the status-derived
    // problem instead of an unbounded allocation, bounded by the smaller of the problem cap and any lower
    // MaxResponseContentBufferSize the caller set for untrusted servers.
    private async Task<Problem> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var maxBytes = (int)Math.Min(_http.MaxResponseContentBufferSize, MaxProblemBodyBytes);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while (buffer.Length < maxBytes
            && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes - buffer.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        var body = DecodeBody(response.Content, buffer);
        return ProblemReader.ReadFrom(body, (int)response.StatusCode, response.ReasonPhrase);
    }

    private static string DecodeBody(HttpContent content, MemoryStream buffer)
    {
        var encoding = Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charSet)
        {
            try
            {
                encoding = Encoding.GetEncoding(charSet.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown charset falls back to UTF-8 rather than failing the problem read.
            }
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
