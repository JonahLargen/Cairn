namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Endpoint metadata forcing a hypermedia format for an endpoint or route group — a built-in
/// <see cref="HypermediaFormat"/>, or a custom formatter identified by its media type.
/// </summary>
internal sealed class HypermediaFormatMetadata
{
    public HypermediaFormatMetadata(HypermediaFormat format) => Format = format;

    public HypermediaFormatMetadata(string mediaType) => MediaType = mediaType;

    public HypermediaFormat Format { get; }

    /// <summary>The forced custom format's media type; <see langword="null"/> when a built-in format is forced.</summary>
    public string? MediaType { get; }
}
