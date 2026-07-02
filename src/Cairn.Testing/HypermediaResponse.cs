using System.Text.Json;

namespace Cairn.Testing;

/// <summary>A link parsed from a Cairn hypermedia response.</summary>
/// <param name="Href">The link's target URI.</param>
/// <param name="Title">An optional human-readable title.</param>
public sealed record HypermediaLink(string Href, string? Title)
{
    /// <summary>An optional secondary key for selecting between links that share a relation (HAL <c>name</c>).</summary>
    public string? Name { get; init; }
}

/// <summary>An affordance (action) parsed from a Cairn hypermedia response.</summary>
/// <param name="Href">The action's target URI.</param>
/// <param name="Method">The HTTP method used to invoke the action.</param>
/// <param name="Title">An optional human-readable title.</param>
public sealed record HypermediaAffordance(string Href, string Method, string? Title);

/// <summary>The links and affordances parsed from a single resource's hypermedia response.</summary>
public sealed class HypermediaResponse
{
    /// <summary>Creates a parsed hypermedia response.</summary>
    public HypermediaResponse(
        IReadOnlyDictionary<string, HypermediaLink> links,
        IReadOnlyDictionary<string, HypermediaAffordance> affordances,
        IReadOnlyDictionary<string, IReadOnlyList<HypermediaLink>>? allLinks = null)
    {
        Links = links;
        Affordances = affordances;
        AllLinks = allLinks ?? links.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<HypermediaLink>)[pair.Value], StringComparer.Ordinal);
    }

    /// <summary>The links, keyed by relation. A relation with several links (a HAL link array) maps to its first; see <see cref="AllLinks"/> for the full set.</summary>
    public IReadOnlyDictionary<string, HypermediaLink> Links { get; }

    /// <summary>Every link for each relation, in document order — including relations emitted as HAL link arrays (e.g. <c>curies</c> or multi-link rels).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HypermediaLink>> AllLinks { get; }

    /// <summary>The affordances, keyed by name (from <c>_actions</c> or HAL-FORMS <c>_templates</c>).</summary>
    public IReadOnlyDictionary<string, HypermediaAffordance> Affordances { get; }

    /// <summary>Parses a single resource's hypermedia from a JSON body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static HypermediaResponse Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var links = new Dictionary<string, HypermediaLink>(StringComparer.Ordinal);
        var allLinks = new Dictionary<string, IReadOnlyList<HypermediaLink>>(StringComparer.Ordinal);
        var affordances = new Dictionary<string, HypermediaAffordance>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("_links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var link in linksElement.EnumerateObject())
                {
                    // A relation's value is a single link object, or a HAL link array (multi-link rels, curies).
                    var parsed = ParseLinkValue(link.Value);
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
                        affordances[action.Name] = new HypermediaAffordance(GetString(action.Value, "href") ?? string.Empty, GetString(action.Value, "method") ?? string.Empty, GetString(action.Value, "title"));
                    }
                }
            }

            if (root.TryGetProperty("_templates", out var templatesElement) && templatesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var template in templatesElement.EnumerateObject())
                {
                    if (template.Value.ValueKind == JsonValueKind.Object)
                    {
                        affordances[template.Name] = new HypermediaAffordance(GetString(template.Value, "target") ?? string.Empty, GetString(template.Value, "method") ?? string.Empty, GetString(template.Value, "title"));
                    }
                }
            }
        }

        return new HypermediaResponse(links, affordances, allLinks);
    }

    private static IReadOnlyList<HypermediaLink> ParseLinkValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return [ParseLink(value)];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<HypermediaLink>();
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    parsed.Add(ParseLink(element));
                }
            }

            return parsed;
        }

        // A scalar or null relation value is malformed; skip it rather than throw.
        return [];
    }

    private static HypermediaLink ParseLink(JsonElement element)
        => new(GetString(element, "href") ?? string.Empty, GetString(element, "title")) { Name = GetString(element, "name") };

    // Guard on Object: TryGetProperty throws InvalidOperationException on a non-object element.
    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Extension methods for reading a <see cref="HypermediaResponse"/> in tests.</summary>
public static class HypermediaResponseExtensions
{
    /// <summary>Parses a hypermedia response from a JSON string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static HypermediaResponse Hypermedia(this string json) => HypermediaResponse.Parse(json);

    /// <summary>Reads and parses a hypermedia response from an <see cref="HttpResponseMessage"/> body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public static async Task<HypermediaResponse> ReadHypermediaAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return HypermediaResponse.Parse(json);
    }
}
