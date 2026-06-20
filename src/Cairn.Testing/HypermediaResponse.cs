using System.Text.Json;

namespace Cairn.Testing;

/// <summary>A link parsed from a Cairn hypermedia response.</summary>
/// <param name="Href">The link's target URI.</param>
/// <param name="Title">An optional human-readable title.</param>
public sealed record HypermediaLink(string Href, string? Title);

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
        IReadOnlyDictionary<string, HypermediaAffordance> affordances)
    {
        Links = links;
        Affordances = affordances;
    }

    /// <summary>The links, keyed by relation.</summary>
    public IReadOnlyDictionary<string, HypermediaLink> Links { get; }

    /// <summary>The affordances, keyed by name (from <c>_actions</c> or HAL-FORMS <c>_templates</c>).</summary>
    public IReadOnlyDictionary<string, HypermediaAffordance> Affordances { get; }

    /// <summary>Parses a single resource's hypermedia from a JSON body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static HypermediaResponse Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var links = new Dictionary<string, HypermediaLink>(StringComparer.Ordinal);
        var affordances = new Dictionary<string, HypermediaAffordance>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("_links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var link in linksElement.EnumerateObject())
                {
                    links[link.Name] = new HypermediaLink(GetString(link.Value, "href") ?? string.Empty, GetString(link.Value, "title"));
                }
            }

            if (root.TryGetProperty("_actions", out var actionsElement) && actionsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var action in actionsElement.EnumerateObject())
                {
                    affordances[action.Name] = new HypermediaAffordance(GetString(action.Value, "href") ?? string.Empty, GetString(action.Value, "method") ?? string.Empty, GetString(action.Value, "title"));
                }
            }

            if (root.TryGetProperty("_templates", out var templatesElement) && templatesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var template in templatesElement.EnumerateObject())
                {
                    affordances[template.Name] = new HypermediaAffordance(GetString(template.Value, "target") ?? string.Empty, GetString(template.Value, "method") ?? string.Empty, GetString(template.Value, "title"));
                }
            }
        }

        return new HypermediaResponse(links, affordances);
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Extension methods for reading a <see cref="HypermediaResponse"/> in tests.</summary>
public static class HypermediaResponseExtensions
{
    /// <summary>Parses a hypermedia response from a JSON string.</summary>
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
