using System.Text.Json;

namespace Cairn.Client;

/// <summary>Parses Cairn hypermedia (<c>_links</c>, <c>_actions</c>, HAL-FORMS <c>_templates</c>) from a resource body.</summary>
internal static class HypermediaParser
{
    public static (IReadOnlyDictionary<string, Link> Links, IReadOnlyDictionary<string, Affordance> Affordances, IReadOnlyDictionary<string, IReadOnlyList<AffordanceField>> Fields) Parse(JsonElement root)
    {
        var links = new Dictionary<string, Link>(StringComparer.Ordinal);
        var affordances = new Dictionary<string, Affordance>(StringComparer.Ordinal);
        var fields = new Dictionary<string, IReadOnlyList<AffordanceField>>(StringComparer.Ordinal);

        if (root.ValueKind != JsonValueKind.Object)
        {
            return (links, affordances, fields);
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
                        Type = GetString(entry.Value, "type"),
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
                    fields[entry.Name] = ParseFields(entry.Value);
                }
            }
        }

        return (links, affordances, fields);
    }

    private static IReadOnlyList<AffordanceField> ParseFields(JsonElement template)
    {
        if (!template.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var fields = new List<AffordanceField>();
        foreach (var property in properties.EnumerateArray())
        {
            if (property.ValueKind == JsonValueKind.Object && GetString(property, "name") is { } name && IsUsable(name))
            {
                fields.Add(new AffordanceField(name)
                {
                    Required = GetBool(property, "required") ?? false,
                    Type = GetString(property, "type"),
                    Regex = GetString(property, "regex"),
                    MaxLength = GetInt(property, "maxLength"),
                    Min = GetDouble(property, "min"),
                    Max = GetDouble(property, "max"),
                });
            }
        }

        return fields;
    }

    // A malformed entry (null/whitespace relation or href) is skipped rather than throwing from the
    // Link/Affordance constructor, so one bad entry never aborts the whole response.
    private static bool IsUsable(string? value) => !string.IsNullOrWhiteSpace(value);

    // Guard on Object: a relation whose value is a JSON array (a valid HAL multi-link rel) or a scalar must be
    // skipped, not throw — TryGetProperty throws InvalidOperationException on a non-object element.
    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? GetInt(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? GetDouble(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
