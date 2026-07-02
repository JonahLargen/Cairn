namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Surfaces the envelope types registered via <c>AddPaging</c>/<c>AddCursorPaging</c> through the Core-level
/// <see cref="IPaginationEnvelopeProvider"/>, so document generators (Cairn.OpenApi, Cairn.Swashbuckle) —
/// which don't reference this assembly — describe adapted envelopes the same way the wire decorates them.
/// </summary>
internal sealed class OptionsPaginationEnvelopeProvider(CairnOptions options) : IPaginationEnvelopeProvider
{
    public bool IsPaginationEnvelope(Type type, out bool cursor) => options.TryGetEnvelopeShape(type, out cursor);
}
