using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

/// <summary>A link in the emitted hypermedia payload.</summary>
internal sealed record HalLink(string Href)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Templated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }
}

/// <summary>The value of a <c>_links</c> relation: a single link object, or a JSON array when several share a rel.</summary>
[JsonConverter(typeof(HalLinkValueJsonConverter))]
internal sealed class HalLinkValue(IReadOnlyList<HalLink> links, bool alwaysArray = false)
{
    public IReadOnlyList<HalLink> Links { get; } = links;

    // curies are always an array, even with a single entry.
    public bool AlwaysArray { get; } = alwaysArray;
}

/// <summary>Writes a single-element relation as a HAL link object and a multi-element relation as a HAL link array.</summary>
internal sealed class HalLinkValueJsonConverter : JsonConverter<HalLinkValue>
{
    public override HalLinkValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, HalLinkValue value, JsonSerializerOptions options)
    {
        if (!value.AlwaysArray && value.Links.Count == 1)
        {
            JsonSerializer.Serialize(writer, value.Links[0], options);
            return;
        }

        writer.WriteStartArray();
        foreach (var link in value.Links)
        {
            JsonSerializer.Serialize(writer, link, options);
        }

        writer.WriteEndArray();
    }
}

/// <summary>An affordance (action) in the emitted hypermedia payload.</summary>
internal sealed record HalAction(string Href, string Method)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore]
    public Type? Input { get; init; }

    [JsonIgnore]
    public string? ContentType { get; init; }
}

/// <summary>An affordance projected into a HAL-FORMS <c>_templates</c> entry.</summary>
internal sealed record HalFormsTemplate(string Method, string Target)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    public string ContentType { get; init; } = "application/json";

    public IReadOnlyList<HalFormsProperty> Properties { get; init; } = [];
}

/// <summary>A field in a HAL-FORMS template, derived from an input type's data annotations.</summary>
internal sealed record HalFormsProperty(string Name)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReadOnly { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Regex { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HalFormsOptions? Options { get; init; }
}

/// <summary>A HAL-FORMS <c>options</c> block enumerating a field's selectable values.</summary>
internal sealed record HalFormsOptions(IReadOnlyList<HalFormsOption> Inline);

/// <summary>One selectable value in a HAL-FORMS <c>options.inline</c> list.</summary>
internal sealed record HalFormsOption(string Prompt, string Value);

/// <summary>The serializable hypermedia computed for a single resource instance.</summary>
internal sealed record ResourceHypermedia(
    IReadOnlyDictionary<string, HalLinkValue>? Links,
    IReadOnlyDictionary<string, HalAction>? Actions,
    IReadOnlyDictionary<string, object>? Embedded = null);

/// <summary>Per-request map from a serializable instance to its computed hypermedia (by reference).</summary>
internal static class CairnLinkStore
{
    private const string ItemsKey = "Cairn.LinkStore";
    private const string FormatKey = "Cairn.Format";

    public static void SetFormat(HttpContext http, HypermediaFormat format) => http.Items[FormatKey] = format;

    public static HypermediaFormat GetFormat(HttpContext http)
        => http.Items[FormatKey] is HypermediaFormat format ? format : HypermediaFormat.Default;

    public static void Record(HttpContext http, object instance, ResourceHypermedia payload)
    {
        if (http.Items[ItemsKey] is not Dictionary<object, ResourceHypermedia> store)
        {
            store = new Dictionary<object, ResourceHypermedia>(ReferenceEqualityComparer.Instance);
            http.Items[ItemsKey] = store;
        }

        store[instance] = payload;
    }

    public static ResourceHypermedia? Lookup(HttpContext http, object instance)
        => http.Items[ItemsKey] is Dictionary<object, ResourceHypermedia> store
            && store.TryGetValue(instance, out var payload)
                ? payload
                : null;
}
