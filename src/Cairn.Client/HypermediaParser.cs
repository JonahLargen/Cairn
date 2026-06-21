using System.Text.Json;

namespace Cairn.Client;

/// <summary>Parses Cairn hypermedia (<c>_links</c>, <c>_actions</c>, HAL-FORMS <c>_templates</c>) from a resource body.</summary>
internal static class HypermediaParser
{
    public static (IReadOnlyDictionary<string, Link> Links, IReadOnlyDictionary<string, Affordance> Affordances) Parse(JsonElement root)
    {
        var links = new Dictionary<string, Link>(StringComparer.Ordinal);
        var affordances = new Dictionary<string, Affordance>(StringComparer.Ordinal);

        if (root.ValueKind != JsonValueKind.Object)
        {
            return (links, affordances);
        }

        if (root.TryGetProperty("_links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in linksElement.EnumerateObject())
            {
                if (IsUsable(entry.Name) && GetString(entry.Value, "href") is { } href && IsUsable(href))
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
                if (IsUsable(entry.Name) && GetString(entry.Value, "href") is { } href && IsUsable(href))
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
                if (IsUsable(entry.Name) && GetString(entry.Value, "target") is { } target && IsUsable(target))
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

    // A malformed entry (null/whitespace relation or href) is skipped rather than throwing from the
    // Link/Affordance constructor, so one bad entry never aborts the whole response.
    private static bool IsUsable(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
