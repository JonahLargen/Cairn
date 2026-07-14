using System.Text.Json.Serialization;

namespace Cairn.AspNetCore.Internal;

/// <summary>The root of an ALPS profile document (the JSON representation wraps everything in an <c>alps</c> member).</summary>
internal sealed record AlpsDocumentRoot([property: JsonPropertyName("alps")] AlpsDocument Alps);

/// <summary>An ALPS profile document body.</summary>
internal sealed record AlpsDocument
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    [JsonPropertyName("doc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlpsDoc? Doc { get; init; }

    [JsonPropertyName("descriptor")]
    public IReadOnlyList<AlpsDescriptor> Descriptors { get; init; } = [];
}

/// <summary>An ALPS <c>doc</c> element: free-text documentation.</summary>
internal sealed record AlpsDoc([property: JsonPropertyName("value")] string Value)
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = "text";
}

/// <summary>
/// An ALPS descriptor: either a definition (with <c>id</c> and <c>type</c>) or a reference to one declared
/// elsewhere in the document (with <c>href</c> only).
/// </summary>
internal sealed record AlpsDescriptor
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("href")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Href { get; init; }

    // The original relation/field name when the id had to be disambiguated within the document.
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("doc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlpsDoc? Doc { get; init; }

    [JsonPropertyName("descriptor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AlpsDescriptor>? Descriptors { get; init; }

    [JsonPropertyName("link")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AlpsLink>? Links { get; init; }
}

/// <summary>An ALPS <c>link</c> element on a descriptor (e.g. a <c>profile</c> link to an embedded child's own document).</summary>
internal sealed record AlpsLink(
    [property: JsonPropertyName("rel")] string Rel,
    [property: JsonPropertyName("href")] string Href);

/// <summary>The index document listing every generated profile.</summary>
internal sealed record AlpsIndex([property: JsonPropertyName("profiles")] IReadOnlyList<AlpsIndexEntry> Profiles);

/// <summary>One profile in the index: the profile name, the CLR resource type it describes, and where the document is served.</summary>
internal sealed record AlpsIndexEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("href")] string Href);

/// <summary>
/// Source-generated JSON metadata for the ALPS documents, so they serialize without reflection-based
/// contracts (the standard Native AOT setup). Indented: profile documents are read by people as much as by
/// tooling.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AlpsDocumentRoot))]
[JsonSerializable(typeof(AlpsIndex))]
internal sealed partial class AlpsJsonContext : JsonSerializerContext;
