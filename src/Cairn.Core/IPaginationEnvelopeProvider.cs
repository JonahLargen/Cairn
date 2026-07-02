namespace Cairn;

/// <summary>
/// Announces the response types the wire treats as pagination envelopes beyond the structural ones (types
/// implementing the pagination interfaces) — i.e. envelope types adapted at registration time, such as
/// Cairn.AspNetCore's <c>AddPaging</c>/<c>AddCursorPaging</c>. Document generators (Cairn.OpenApi,
/// Cairn.Swashbuckle) reference only Cairn.Core, so this is how they learn about adapted envelopes and
/// describe them the same way the wire decorates them.
/// </summary>
public interface IPaginationEnvelopeProvider
{
    /// <summary>
    /// Whether <paramref name="type"/> is a registered pagination envelope; when it is,
    /// <paramref name="cursor"/> says whether it carries cursor (vs. offset) navigation links.
    /// </summary>
    bool IsPaginationEnvelope(Type type, out bool cursor);
}
