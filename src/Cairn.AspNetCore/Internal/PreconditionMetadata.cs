namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Endpoint metadata marking an endpoint that evaluates write preconditions with
/// <see cref="CairnPreconditions.Evaluate"/>, left by <c>WithPreconditions(...)</c>. It carries no data — its
/// presence is the signal the OpenAPI integrations (Cairn.OpenApi, Cairn.Swashbuckle) match on to document a
/// <c>412 Precondition Failed</c> response. The companion <see cref="PreconditionRequiredMetadata"/> marker,
/// added only for a require-<c>If-Match</c> endpoint, drives the <c>428</c>; keeping the two as distinct types
/// lets the document generators — which do not reference this assembly — detect the require-<c>If-Match</c> case
/// by presence, matching by full type name exactly as they do for the ETag and deprecation markers, without
/// reflecting a value across the assembly boundary.
/// </summary>
internal sealed class PreconditionMetadata
{
    /// <summary>The shared instance; the marker is stateless, so one instance serves every endpoint.</summary>
    public static readonly PreconditionMetadata Instance = new();

    private PreconditionMetadata()
    {
    }
}
