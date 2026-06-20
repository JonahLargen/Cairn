using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

/// <summary>Endpoint metadata carrying a per-route cursor-to-URL function for cursor pagination links.</summary>
internal sealed class CursorLinkMetadata(Func<HttpRequest, string, string> cursorLink)
{
    public Func<HttpRequest, string, string> CursorLink { get; } = cursorLink;
}
