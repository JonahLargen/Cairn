namespace Cairn.AspNetCore.Internal;

/// <summary>Endpoint metadata forcing a hypermedia format for an endpoint or route group.</summary>
internal sealed class HypermediaFormatMetadata(HypermediaFormat format)
{
    public HypermediaFormat Format { get; } = format;
}
