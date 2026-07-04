using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

// Every wire property below pins its JSON name with [JsonPropertyName]: HAL/HAL-FORMS mandate these exact
// member names, so the host app's PropertyNamingPolicy (PascalCase, snake_case, ...) must never rename them.

/// <summary>A link in the emitted hypermedia payload.</summary>
internal sealed record HalLink([property: JsonPropertyName("href")] string Href)
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("templated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Templated { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("deprecation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Deprecation { get; init; }

    [JsonPropertyName("hreflang")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hreflang { get; init; }

    [JsonPropertyName("profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Profile { get; init; }
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
        // Resolve HalLink's contract from the options' resolver chain (CairnJsonContext supplies it under a
        // source-gen-only resolver) so emission never needs reflection-based contracts.
        var linkInfo = options.GetTypeInfo(typeof(HalLink));
        if (!value.AlwaysArray && value.Links.Count == 1)
        {
            JsonSerializer.Serialize(writer, value.Links[0], linkInfo);
            return;
        }

        writer.WriteStartArray();
        foreach (var link in value.Links)
        {
            JsonSerializer.Serialize(writer, link, linkInfo);
        }

        writer.WriteEndArray();
    }
}

/// <summary>An affordance (action) in the emitted hypermedia payload.</summary>
internal sealed record HalAction(
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("method")] string Method)
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    // Internal (not public + [JsonIgnore]) so the property stays out of the JSON contract entirely: the
    // source generator emits accessor delegates for public properties even when ignored, and those
    // delegates trip the trim analyzer (IL2111/IL2062) on an annotated property.
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    internal Type? Input { get; init; }

    [JsonIgnore]
    public string? ContentType { get; init; }

    // Emitted under the reserved "default" HAL-FORMS template key rather than its name.
    [JsonIgnore]
    public bool IsDefault { get; init; }
}

/// <summary>An affordance projected into a HAL-FORMS <c>_templates</c> entry.</summary>
internal sealed record HalFormsTemplate(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("target")] string Target)
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "application/json";

    [JsonPropertyName("properties")]
    public IReadOnlyList<HalFormsProperty> Properties { get; init; } = [];
}

/// <summary>A field in a HAL-FORMS template, derived from an input type's data annotations.</summary>
internal sealed record HalFormsProperty([property: JsonPropertyName("name")] string Name)
{
    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; init; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; init; }

    [JsonPropertyName("readOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReadOnly { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("placeholder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; init; }

    [JsonPropertyName("regex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Regex { get; init; }

    [JsonPropertyName("minLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; init; }

    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; init; }

    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; init; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; init; }

    /// <summary>The field's default value (HAL-FORMS <c>value</c>).</summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HalFormsOptions? Options { get; init; }
}

/// <summary>A HAL-FORMS <c>options</c> block enumerating a field's selectable values.</summary>
internal sealed record HalFormsOptions([property: JsonPropertyName("inline")] IReadOnlyList<HalFormsOption> Inline);

/// <summary>One selectable value in a HAL-FORMS <c>options.inline</c> list.</summary>
internal sealed record HalFormsOption(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("value")] string Value);

/// <summary>The serializable hypermedia computed for a single resource instance.</summary>
internal sealed record ResourceHypermedia(
    IReadOnlyDictionary<string, HalLinkValue>? Links,
    IReadOnlyDictionary<string, HalAction>? Actions,
    IReadOnlyDictionary<string, object>? Embedded = null)
{
    private HypermediaDocument? _document;

    /// <summary>The formatter-facing view of this hypermedia, built once per resource on first use.</summary>
    public HypermediaDocument ToDocument() => _document ??= Build();

    private HypermediaDocument Build()
    {
        List<Link> links = [];
        if (Links is not null)
        {
            foreach (var (relation, value) in Links)
            {
                foreach (var link in value.Links)
                {
                    links.Add(new Link(relation, link.Href, link.Templated ?? false)
                    {
                        Name = link.Name,
                        Title = link.Title,
                        Type = link.Type,
                        Deprecation = link.Deprecation,
                        Hreflang = link.Hreflang,
                        Profile = link.Profile,
                    });
                }
            }
        }

        List<Affordance> affordances = [];
        if (Actions is not null)
        {
            foreach (var (name, action) in Actions)
            {
                affordances.Add(new Affordance(name, action.Href, action.Method)
                {
                    Title = action.Title,
                    Input = action.Input,
                    ContentType = action.ContentType,
                    IsDefault = action.IsDefault,
                });
            }
        }

        return new HypermediaDocument(links, affordances, Embedded);
    }
}

/// <summary>Per-request map from a serializable instance to its computed hypermedia (by reference).</summary>
internal static class CairnLinkStore
{
    private const string ItemsKey = "Cairn.LinkStore";
    private const string FormatKey = "Cairn.Format";
    private const string FormatterKey = "Cairn.Formatter";
    private const string MaterializedKey = "Cairn.Materialized";

    public static void SetFormat(HttpContext http, HypermediaFormat format) => http.Items[FormatKey] = format;

    public static HypermediaFormat GetFormat(HttpContext http)
        => http.Items[FormatKey] is HypermediaFormat format ? format : HypermediaFormat.Default;

    /// <summary>The custom formatter selected for this request, if any — it supersedes the built-in emission.</summary>
    public static void SetFormatter(HttpContext http, IHypermediaFormatter? formatter)
    {
        if (formatter is not null)
        {
            http.Items[FormatterKey] = formatter;
        }
    }

    public static IHypermediaFormatter? GetFormatter(HttpContext http)
        => http.Items[FormatterKey] as IHypermediaFormatter;

    public static void Record(HttpContext http, object instance, ResourceHypermedia payload)
    {
        if (http.Items[ItemsKey] is not Dictionary<object, Entry> store)
        {
            store = new Dictionary<object, Entry>(ReferenceEqualityComparer.Instance);
            http.Items[ItemsKey] = store;
        }

        store[instance] = new Entry(payload);
    }

    /// <summary>Emit-stage lookup: returns the hypermedia for <paramref name="instance"/> and marks it emitted.</summary>
    public static ResourceHypermedia? Lookup(HttpContext http, object instance)
    {
        if (Store(http) is { } store && store.TryGetValue(instance, out var entry))
        {
            entry.Emitted = true;
            return entry.Payload;
        }

        return null;
    }

    /// <summary>Whether hypermedia was recorded for <paramref name="instance"/>, without marking it emitted.</summary>
    public static bool Has(HttpContext http, object instance)
        => Store(http)?.ContainsKey(instance) == true;

    public static bool HasEntries(HttpContext http) => Store(http) is { Count: > 0 };

    /// <summary>
    /// Registers <paramref name="buffer"/> as the once-enumerated materialization of the deferred
    /// <paramref name="sequence"/> an envelope exposes as its items. The emit stage substitutes it for the
    /// sequence at serialization (see <see cref="CairnLinkInjectionModifier"/>) so the query runs once and the
    /// item links stay correlated — without the compute stage mutating the (possibly shared) envelope. Keyed by
    /// reference, per request.
    /// </summary>
    public static void RecordMaterialized(HttpContext http, object sequence, IEnumerable buffer)
    {
        if (http.Items[MaterializedKey] is not Dictionary<object, IEnumerable> map)
        {
            map = new Dictionary<object, IEnumerable>(ReferenceEqualityComparer.Instance);
            http.Items[MaterializedKey] = map;
        }

        map[sequence] = buffer;
    }

    /// <summary>The buffer registered for a deferred <paramref name="sequence"/>, if one was materialized this request.</summary>
    public static bool TryGetMaterialized(HttpContext http, object sequence, [NotNullWhen(true)] out IEnumerable? buffer)
    {
        if (http.Items[MaterializedKey] is Dictionary<object, IEnumerable> map && map.TryGetValue(sequence, out var found))
        {
            buffer = found;
            return true;
        }

        buffer = null;
        return false;
    }

    /// <summary>
    /// The distinct types whose recorded hypermedia was never looked up by the emit stage — the reference
    /// correlation between compute and serialization broke for them. Boxed value types are excluded; they
    /// already get a dedicated warning at record time.
    /// </summary>
    public static IReadOnlyList<Type> UnemittedTypes(HttpContext http)
    {
        if (Store(http) is not { } store)
        {
            return [];
        }

        List<Type>? missed = null;
        foreach (var (instance, entry) in store)
        {
            var type = instance.GetType();
            if (entry.Emitted || type.IsValueType)
            {
                continue;
            }

            missed ??= [];
            if (!missed.Contains(type))
            {
                missed.Add(type);
            }
        }

        return missed ?? [];
    }

    private static Dictionary<object, Entry>? Store(HttpContext http)
        => http.Items[ItemsKey] as Dictionary<object, Entry>;

    // Emitted flips when the serializer looks the instance up, powering the never-emitted diagnostic.
    private sealed class Entry(ResourceHypermedia payload)
    {
        public ResourceHypermedia Payload { get; } = payload;

        public bool Emitted { get; set; }
    }
}
