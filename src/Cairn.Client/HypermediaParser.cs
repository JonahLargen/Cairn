using System.Text.Json;

namespace Cairn.Client;

/// <summary>The hypermedia parsed from a resource body.</summary>
internal sealed record ParsedHypermedia(
    IReadOnlyDictionary<string, IReadOnlyList<Link>> Links,
    IReadOnlyDictionary<string, Affordance> Affordances,
    IReadOnlyDictionary<string, IReadOnlyList<AffordanceField>> Fields,
    IReadOnlyList<Curie> Curies,
    JsonElement Embedded)
{
    public static readonly ParsedHypermedia Empty = new(
        new Dictionary<string, IReadOnlyList<Link>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, Affordance>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<AffordanceField>>(StringComparer.OrdinalIgnoreCase),
        [],
        default);
}

/// <summary>Parses Cairn hypermedia (<c>_links</c>, <c>_actions</c>, HAL-FORMS <c>_templates</c>) from a resource body.</summary>
internal static class HypermediaParser
{
    public static ParsedHypermedia Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ParsedHypermedia.Empty;
        }

        // Relation types are case-insensitive (RFC 8288 §2.1), matching how the server compares them.
        var links = new Dictionary<string, IReadOnlyList<Link>>(StringComparer.OrdinalIgnoreCase);
        var affordances = new Dictionary<string, Affordance>(StringComparer.OrdinalIgnoreCase);
        var fields = new Dictionary<string, IReadOnlyList<AffordanceField>>(StringComparer.OrdinalIgnoreCase);
        List<Curie>? curies = null;

        if (root.TryGetProperty("_links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in linksElement.EnumerateObject())
            {
                if (!IsUsable(entry.Name))
                {
                    continue;
                }

                // A relation's value is a single link object, or a HAL link array of them.
                var parsed = ParseLinks(entry.Name, entry.Value);
                if (parsed.Count > 0)
                {
                    links[entry.Name] = parsed;
                }

                // The reserved curies relation additionally defines prefixes for resolving relation docs.
                if (string.Equals(entry.Name, "curies", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var link in parsed)
                    {
                        if (IsUsable(link.Name))
                        {
                            (curies ??= []).Add(new Curie(link.Name!, link.Href, link.Templated));
                        }
                    }
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
            // HAL-FORMS: target is optional — a template without one is submitted to the resource itself.
            var selfHref = links.TryGetValue("self", out var selfLinks) && selfLinks.Count > 0 ? selfLinks[0].Href : null;
            foreach (var entry in templatesElement.EnumerateObject())
            {
                var target = GetString(entry.Value, "target");
                if (!IsUsable(target))
                {
                    target = selfHref;
                }

                if (IsUsable(entry.Name) && IsUsable(target))
                {
                    affordances[entry.Name] = new Affordance(entry.Name, target!, GetString(entry.Value, "method") ?? "GET")
                    {
                        Title = GetString(entry.Value, "title"),
                        ContentType = GetString(entry.Value, "contentType"),
                    };
                    fields[entry.Name] = ParseFields(entry.Value);
                }
            }
        }

        // Clone so the embedded subtree survives the source JsonDocument being disposed; resolved on demand.
        var embedded = root.TryGetProperty("_embedded", out var embeddedElement) && embeddedElement.ValueKind == JsonValueKind.Object
            ? embeddedElement.Clone()
            : default;

        return new ParsedHypermedia(links, affordances, fields, curies ?? [], embedded);
    }

    private static IReadOnlyList<Link> ParseLinks(string relation, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<Link>();
            foreach (var element in value.EnumerateArray())
            {
                if (ParseLink(relation, element) is { } link)
                {
                    list.Add(link);
                }
            }

            return list;
        }

        return ParseLink(relation, value) is { } single ? [single] : [];
    }

    private static Link? ParseLink(string relation, JsonElement element)
        => element.ValueKind == JsonValueKind.Object && GetString(element, "href") is { } href && IsUsable(href)
            ? new Link(relation, href, GetBool(element, "templated") ?? false)
            {
                Title = GetString(element, "title"),
                Type = GetString(element, "type"),
                Name = GetString(element, "name"),
                Deprecation = GetString(element, "deprecation"),
                Hreflang = GetString(element, "hreflang"),
                Profile = GetString(element, "profile"),
            }
            : null;

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
                    Prompt = GetString(property, "prompt"),
                    Required = GetBool(property, "required") ?? false,
                    ReadOnly = GetBool(property, "readOnly") ?? false,
                    Type = GetString(property, "type"),
                    Value = GetString(property, "value"),
                    Templated = GetBool(property, "templated") ?? false,
                    Placeholder = GetString(property, "placeholder"),
                    Regex = GetString(property, "regex"),
                    MinLength = GetInt(property, "minLength"),
                    MaxLength = GetInt(property, "maxLength"),
                    Min = GetDouble(property, "min"),
                    Max = GetDouble(property, "max"),
                    Step = GetDouble(property, "step"),
                    Cols = GetInt(property, "cols"),
                    Rows = GetInt(property, "rows"),
                    Options = ParseOptions(property),
                    OptionsLink = property.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
                        && options.TryGetProperty("link", out var optionsLink) ? GetString(optionsLink, "href") : null,
                    SelectedValues = ParseSelectedValues(property),
                });
            }
        }

        return fields;
    }

    private static IReadOnlyList<AffordanceFieldOption> ParseOptions(JsonElement property)
    {
        if (!property.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("inline", out var inline) || inline.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<AffordanceFieldOption>();
        foreach (var option in inline.EnumerateArray())
        {
            // An inline option is either a string or an object with a "value" (and an optional "prompt").
            var value = option.ValueKind == JsonValueKind.String ? option.GetString() : GetString(option, "value");
            if (IsUsable(value))
            {
                values.Add(new AffordanceFieldOption(value!) { Prompt = GetString(option, "prompt") });
            }
        }

        return values;
    }

    private static IReadOnlyList<string> ParseSelectedValues(JsonElement property)
    {
        if (!property.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("selectedValues", out var selected) || selected.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var entry in selected.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
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
