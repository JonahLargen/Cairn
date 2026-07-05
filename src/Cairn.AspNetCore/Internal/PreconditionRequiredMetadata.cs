namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Endpoint metadata marking an endpoint that requires a conditional header — <c>WithPreconditions(requireIfMatch:
/// true)</c> — added alongside <see cref="PreconditionMetadata"/>. Its presence is the signal the OpenAPI
/// integrations match on to document a <c>428 Precondition Required</c> response, matching the status
/// <see cref="CairnPreconditions.Evaluate"/> returns when a request carries no conditional header and
/// <c>If-Match</c> is required. Stateless, like the other Cairn endpoint markers.
/// </summary>
internal sealed class PreconditionRequiredMetadata
{
    /// <summary>The shared instance; the marker is stateless, so one instance serves every endpoint.</summary>
    public static readonly PreconditionRequiredMetadata Instance = new();

    private PreconditionRequiredMetadata()
    {
    }
}
