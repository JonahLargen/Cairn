using System.Text.Json;

namespace Cairn.Testing;

/// <summary>A link parsed from a Cairn hypermedia response.</summary>
/// <param name="Href">The link's target URI.</param>
/// <param name="Title">An optional human-readable title.</param>
public sealed record HypermediaLink(string Href, string? Title)
{
    /// <summary>An optional secondary key for selecting between links that share a relation (HAL <c>name</c>).</summary>
    public string? Name { get; init; }

    /// <summary>Whether <see cref="Href"/> is a URI template (HAL <c>templated</c>).</summary>
    public bool Templated { get; init; }

    /// <summary>An optional media type hint for the target (RFC 8288 <c>type</c>).</summary>
    public string? Type { get; init; }

    /// <summary>An optional URL documenting the link's deprecation (HAL <c>deprecation</c>).</summary>
    public string? Deprecation { get; init; }

    /// <summary>An optional language hint for the target (RFC 8288 <c>hreflang</c>).</summary>
    public string? Hreflang { get; init; }

    /// <summary>An optional profile URI for the target (RFC 6906 <c>profile</c>).</summary>
    public string? Profile { get; init; }
}

/// <summary>An affordance (action) parsed from a Cairn hypermedia response.</summary>
/// <param name="Href">The action's target URI.</param>
/// <param name="Method">The HTTP method used to invoke the action.</param>
/// <param name="Title">An optional human-readable title.</param>
public sealed record HypermediaAffordance(string Href, string Method, string? Title)
{
    /// <summary>The media type the action's request body is submitted as (HAL-FORMS <c>contentType</c>); carried over from the backing template, or read from <c>_actions</c> when present.</summary>
    public string? ContentType { get; init; }
}

/// <summary>The links and affordances parsed from a single resource's hypermedia response.</summary>
public sealed class HypermediaResponse
{
    /// <summary>Creates a parsed hypermedia response.</summary>
    public HypermediaResponse(
        IReadOnlyDictionary<string, HypermediaLink> links,
        IReadOnlyDictionary<string, HypermediaAffordance> affordances,
        IReadOnlyDictionary<string, IReadOnlyList<HypermediaLink>>? allLinks = null,
        IReadOnlyDictionary<string, IReadOnlyList<HypermediaResponse>>? embedded = null,
        IReadOnlyDictionary<string, HypermediaTemplate>? templates = null)
    {
        Links = links;
        Affordances = affordances;
        AllLinks = allLinks ?? links.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<HypermediaLink>)[pair.Value], StringComparer.Ordinal);
        Embedded = embedded ?? new Dictionary<string, IReadOnlyList<HypermediaResponse>>(StringComparer.Ordinal);
        Templates = templates ?? new Dictionary<string, HypermediaTemplate>(StringComparer.Ordinal);
    }

    /// <summary>The links, keyed by relation. A relation with several links (a HAL link array) maps to its first; see <see cref="AllLinks"/> for the full set.</summary>
    public IReadOnlyDictionary<string, HypermediaLink> Links { get; }

    /// <summary>Every link for each relation, in document order — including relations emitted as HAL link arrays (e.g. <c>curies</c> or multi-link rels).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HypermediaLink>> AllLinks { get; }

    /// <summary>The affordances, keyed by name (from <c>_actions</c> or HAL-FORMS <c>_templates</c>).</summary>
    public IReadOnlyDictionary<string, HypermediaAffordance> Affordances { get; }

    /// <summary>The embedded resources (<c>_embedded</c>), keyed by relation. A single embed parses as a one-element list; a collection embed keeps document order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HypermediaResponse>> Embedded { get; }

    /// <summary>The HAL-FORMS templates (<c>_templates</c>), keyed by name, with their field descriptions.</summary>
    public IReadOnlyDictionary<string, HypermediaTemplate> Templates { get; }

    /// <summary>Parses a single resource's hypermedia from a JSON body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="FormatException">A link or action has a missing or empty <c>href</c> — a malformed payload that must fail the test rather than pass assertions silently — or the root is a JSON array (use <see cref="ParseAll"/> for array-root responses).</exception>
    public static HypermediaResponse Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            // Silently returning an empty response would let every negative assertion pass on a body the
            // caller never meant to treat as a single resource.
            throw new FormatException("The response root is a JSON array; use HypermediaResponse.ParseAll to parse each element's hypermedia.");
        }

        return ParseResource(document.RootElement);
    }

    /// <summary>Parses the hypermedia of each element of an array-root JSON body (e.g. a bare collection response).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="FormatException">The root is not a JSON array, or a link or action has a missing or empty <c>href</c>.</exception>
    public static IReadOnlyList<HypermediaResponse> ParseAll(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The response root is not a JSON array; use HypermediaResponse.Parse for a single resource.");
        }

        var resources = new List<HypermediaResponse>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            resources.Add(ParseResource(element));
        }

        return resources;
    }

    private static HypermediaResponse ParseResource(JsonElement root)
    {
        var links = new Dictionary<string, HypermediaLink>(StringComparer.Ordinal);
        var allLinks = new Dictionary<string, IReadOnlyList<HypermediaLink>>(StringComparer.Ordinal);
        var affordances = new Dictionary<string, HypermediaAffordance>(StringComparer.Ordinal);
        var embedded = new Dictionary<string, IReadOnlyList<HypermediaResponse>>(StringComparer.Ordinal);
        var templates = new Dictionary<string, HypermediaTemplate>(StringComparer.Ordinal);

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("_links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var link in linksElement.EnumerateObject())
                {
                    // A relation's value is a single link object, or a HAL link array (multi-link rels, curies).
                    var parsed = ParseLinkValue(link.Name, link.Value);
                    if (parsed.Count > 0)
                    {
                        allLinks[link.Name] = parsed;
                        links[link.Name] = parsed[0];
                    }
                }
            }

            if (root.TryGetProperty("_actions", out var actionsElement) && actionsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var action in actionsElement.EnumerateObject())
                {
                    if (action.Value.ValueKind == JsonValueKind.Object)
                    {
                        var href = GetString(action.Value, "href");
                        if (string.IsNullOrEmpty(href))
                        {
                            throw new FormatException($"The '{action.Name}' action is malformed: its 'href' is missing or empty.");
                        }

                        // An absent method defaults to GET, matching the client's parser and HAL-FORMS.
                        affordances[action.Name] = new HypermediaAffordance(href, GetString(action.Value, "method") ?? "GET", GetString(action.Value, "title"))
                        {
                            ContentType = GetString(action.Value, "contentType"),
                        };
                    }
                }
            }

            if (root.TryGetProperty("_templates", out var templatesElement) && templatesElement.ValueKind == JsonValueKind.Object)
            {
                var selfHref = links.TryGetValue("self", out var self) ? self.Href : null;
                foreach (var template in templatesElement.EnumerateObject())
                {
                    if (template.Value.ValueKind == JsonValueKind.Object)
                    {
                        var parsed = ParseTemplate(template.Name, template.Value, selfHref);
                        templates[template.Name] = parsed;
                        affordances[template.Name] = new HypermediaAffordance(parsed.Target, parsed.Method, parsed.Title)
                        {
                            ContentType = parsed.ContentType,
                        };
                    }
                }
            }

            if (root.TryGetProperty("_embedded", out var embeddedElement) && embeddedElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var embed in embeddedElement.EnumerateObject())
                {
                    // A single embed is an object; a collection embed is an array of objects.
                    var resources = ParseEmbeddedValue(embed.Value);
                    if (resources.Count > 0)
                    {
                        embedded[embed.Name] = resources;
                    }
                }
            }
        }

        return new HypermediaResponse(links, affordances, allLinks, embedded, templates);
    }

    private static IReadOnlyList<HypermediaLink> ParseLinkValue(string relation, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return [ParseLink(relation, value)];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<HypermediaLink>();
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    parsed.Add(ParseLink(relation, element));
                }
            }

            return parsed;
        }

        // A scalar or null relation value is malformed; skip it rather than throw.
        return [];
    }

    private static HypermediaLink ParseLink(string relation, JsonElement element)
    {
        var href = GetString(element, "href");
        if (string.IsNullOrEmpty(href))
        {
            // A link object without an href is malformed — fail loudly instead of letting assertions pass on "".
            throw new FormatException($"The '{relation}' link is malformed: its 'href' is missing or empty.");
        }

        return new HypermediaLink(href, GetString(element, "title"))
        {
            Name = GetString(element, "name"),
            Templated = GetBool(element, "templated") ?? false,
            Type = GetString(element, "type"),
            Deprecation = GetString(element, "deprecation"),
            Hreflang = GetString(element, "hreflang"),
            Profile = GetString(element, "profile"),
        };
    }

    private static HypermediaTemplate ParseTemplate(string name, JsonElement element, string? selfHref)
    {
        string target;
        if (element.TryGetProperty("target", out var targetValue))
        {
            target = targetValue.ValueKind == JsonValueKind.String ? targetValue.GetString()! : string.Empty;
            if (target.Length == 0)
            {
                throw new FormatException($"The '{name}' template is malformed: its 'target' must be a non-empty string when present.");
            }
        }
        else
        {
            // HAL-FORMS: an absent target means the form is submitted to the resource itself.
            target = selfHref ?? string.Empty;
        }

        // HAL-FORMS defaults an absent method to GET.
        return new HypermediaTemplate(target, GetString(element, "method") ?? "GET", GetString(element, "title"))
        {
            ContentType = GetString(element, "contentType"),
            Fields = ParseFields(element),
        };
    }

    private static IReadOnlyList<HypermediaTemplateField> ParseFields(JsonElement template)
    {
        if (!template.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var fields = new List<HypermediaTemplateField>();
        foreach (var property in properties.EnumerateArray())
        {
            var name = GetString(property, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            fields.Add(new HypermediaTemplateField(name)
            {
                Required = GetBool(property, "required") ?? false,
                ReadOnly = GetBool(property, "readOnly") ?? false,
                Type = GetString(property, "type"),
                Regex = GetString(property, "regex"),
                Prompt = GetString(property, "prompt"),
                Placeholder = GetString(property, "placeholder"),
                Value = GetString(property, "value"),
                MaxLength = GetInt(property, "maxLength"),
                Min = GetDouble(property, "min"),
                Max = GetDouble(property, "max"),
                Options = ParseOptions(property),
            });
        }

        return fields;
    }

    private static IReadOnlyList<string> ParseOptions(JsonElement field)
    {
        if (field.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("inline", out var inline) && inline.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>();
            foreach (var option in inline.EnumerateArray())
            {
                // An inline option is either a bare string or an object with a "value".
                var value = option.ValueKind == JsonValueKind.String ? option.GetString() : GetString(option, "value");
                if (!string.IsNullOrEmpty(value))
                {
                    values.Add(value!);
                }
            }

            return values;
        }

        return [];
    }

    private static IReadOnlyList<HypermediaResponse> ParseEmbeddedValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return [ParseResource(value)];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var resources = new List<HypermediaResponse>();
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    resources.Add(ParseResource(element));
                }
            }

            return resources;
        }

        // A scalar or null embed value is malformed; skip it rather than throw.
        return [];
    }

    // Guard on Object: TryGetProperty throws InvalidOperationException on a non-object element.
    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? GetInt(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

    private static double? GetDouble(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : null;
}

/// <summary>Extension methods for reading a <see cref="HypermediaResponse"/> in tests.</summary>
public static class HypermediaResponseExtensions
{
    /// <summary>Reads and parses a hypermedia response from an <see cref="HttpResponseMessage"/> body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    /// <exception cref="FormatException">A link or action has a missing or empty <c>href</c>.</exception>
    public static async Task<HypermediaResponse> ReadHypermediaAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return HypermediaResponse.Parse(json);
    }

    /// <summary>Reads an array-root response body (e.g. a bare collection) and parses each element's hypermedia.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    /// <exception cref="FormatException">The root is not a JSON array, or a link or action has a missing or empty <c>href</c>.</exception>
    public static async Task<IReadOnlyList<HypermediaResponse>> ReadHypermediaListAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return HypermediaResponse.ParseAll(json);
    }

    /// <summary>
    /// Sends a GET to <paramref name="url"/> and parses the hypermedia from the response body. Works with any
    /// <see cref="HttpClient"/>, including one created by a <c>WebApplicationFactory</c> or <c>TestServer</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="url"/> is null.</exception>
    /// <exception cref="CairnAssertionException">The response status code is not a success code.</exception>
    /// <exception cref="FormatException">A link or action has a missing or empty <c>href</c>.</exception>
    public static async Task<HypermediaResponse> GetHypermediaAsync(this HttpClient client, string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CairnAssertionException($"Expected GET {url} to succeed, but it returned {(int)response.StatusCode} ({response.StatusCode}).");
        }

        return await response.ReadHypermediaAsync(cancellationToken).ConfigureAwait(false);
    }
}
