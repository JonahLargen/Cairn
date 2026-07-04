namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Endpoint metadata marking an endpoint that emits an <c>ETag</c> (and answers a matching
/// <c>If-None-Match</c> with <c>304 Not Modified</c>) via <see cref="CairnETagExtensions.WithETag"/>. It
/// carries no data — its presence is the signal the OpenAPI integrations (Cairn.OpenApi, Cairn.Swashbuckle)
/// match on to document the <c>ETag</c> response header and the <c>304</c> response. The tag itself is derived
/// per response by the endpoint filter, so nothing about it is knowable at document-generation time.
/// </summary>
internal sealed class ETagMetadata
{
    /// <summary>The shared instance; the marker is stateless, so one instance serves every endpoint.</summary>
    public static readonly ETagMetadata Instance = new();

    private ETagMetadata()
    {
    }
}
