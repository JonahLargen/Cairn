using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore.Internal;

/// <summary>Endpoint metadata carrying a per-route page-to-URL function for pagination links.</summary>
internal sealed class PageLinkMetadata(Func<HttpRequest, int, string> pageLink)
{
    public Func<HttpRequest, int, string> PageLink { get; } = pageLink;
}
