using System.Text.Json;

namespace Cairn.Client;

/// <summary>Parses Cairn hypermedia (<c>_links</c>, <c>_actions</c>, HAL-FORMS <c>_templates</c>) from a JSON body.</summary>
internal static class HypermediaParser
{
    public static (IReadOnlyDictionary<string, Link> Links, IReadOnlyDictionary<string, Affordance> Affordances) Parse(string json)
    {
        var links = new Dictionary<string, Link>(StringComparer.Ordinal);
        var affordances = new Dictionary<string, Affordance>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (links, affordances);
        }

        if (root.TryGetProperty("_links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in linksElement.EnumerateObject())
            {
                if (GetString(entry.Value, "href") is { Length: > 0 } href)
                {
                    links[entry.Name] = new Link(entry.Name, href, GetBool(entry.Value, "templated") ?? false)
                    {
                        Title = GetString(entry.Value, "title"),
                    };
                }
            }
        }

        if (root.TryGetProperty("_actions", out var actionsElement) && actionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in actionsElement.EnumerateObject())
            {
                if (GetString(entry.Value, "href") is { Length: > 0 } href)
                {
                    affordances[entry.Name] = new Affordance(entry.Name, href, GetString(entry.Value, "method") ?? "GET")
                    {
                        Title = GetString(entry.Value, "title"),
                    };
                }
            }
        }

        if (root.TryGetProperty("_templates", out var templatesElement) && templatesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in templatesElement.EnumerateObject())
            {
                if (GetString(entry.Value, "target") is { Length: > 0 } target)
                {
                    affordances[entry.Name] = new Affordance(entry.Name, target, GetString(entry.Value, "method") ?? "GET")
                    {
                        Title = GetString(entry.Value, "title"),
                    };
                }
            }
        }

        return (links, affordances);
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
