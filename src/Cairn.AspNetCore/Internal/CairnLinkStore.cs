using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

/// <summary>A link in the emitted hypermedia payload.</summary>
internal sealed record HalLink(string Href)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Templated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }
}

/// <summary>An affordance (action) in the emitted hypermedia payload.</summary>
internal sealed record HalAction(string Href, string Method)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore]
    public Type? Input { get; init; }
}

/// <summary>An affordance projected into a HAL-FORMS <c>_templates</c> entry.</summary>
internal sealed record HalFormsTemplate(string Method, string Target)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    public IReadOnlyList<HalFormsProperty> Properties { get; init; } = [];
}

/// <summary>A field in a HAL-FORMS template, derived from an input type's data annotations.</summary>
internal sealed record HalFormsProperty(string Name)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Regex { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; init; }
}

/// <summary>The serializable hypermedia computed for a single resource instance.</summary>
internal sealed record ResourceHypermedia(
    IReadOnlyDictionary<string, HalLink>? Links,
    IReadOnlyDictionary<string, HalAction>? Actions);

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

    /// <summary>Whether any hypermedia was recorded for this request (i.e. the response was actually decorated).</summary>
    public static bool HasRecorded(HttpContext http)
        => http.Items[ItemsKey] is Dictionary<object, ResourceHypermedia> { Count: > 0 };
}
