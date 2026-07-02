using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Cairn.AspNetCore.Internal;

/// <summary>Builds pagination links for offset and cursor paged resources.</summary>
internal static class PaginationLinks
{
    /// <summary>Offset links: self/first/prev/next/last derived from the page number.</summary>
    public static IReadOnlyDictionary<string, HalLink> BuildOffset(IPagedResource paged, Func<int, string> pageUrl)
    {
        var links = new Dictionary<string, HalLink>
        {
            ["self"] = new(pageUrl(paged.Page)),
        };

        if (paged.TotalPages > 0)
        {
            links["first"] = new(pageUrl(1));
            links["last"] = new(pageUrl(paged.TotalPages));
        }

        // Clamp prev to the last valid page so an over-range request (Page > TotalPages) still points back
        // into range rather than to a non-existent page.
        if (paged.Page > 1 && paged.TotalPages > 0)
        {
            links["prev"] = new(pageUrl(Math.Min(paged.Page - 1, paged.TotalPages)));
        }

        if (paged.Page < paged.TotalPages)
        {
            links["next"] = new(pageUrl(paged.Page + 1));
        }

        return links;
    }

    /// <summary>Cursor links: self is the current URL; next/prev come from the app-supplied cursors.</summary>
    public static IReadOnlyDictionary<string, HalLink> BuildCursor(HttpRequest request, ICursorPagedResource cursor, Func<string, string> cursorUrl, CairnOptions options)
    {
        var links = new Dictionary<string, HalLink>
        {
            ["self"] = new(CurrentUrl(request, options)),
        };

        if (cursor.Next is { Length: > 0 } next)
        {
            links["next"] = new(cursorUrl(next));
        }

        if (cursor.Prev is { Length: > 0 } prev)
        {
            links["prev"] = new(cursorUrl(prev));
        }

        return links;
    }

    /// <summary>The default page URL: the current request URL with <paramref name="pageParameter"/> set to the page number.</summary>
    public static string DefaultPageUrl(HttpRequest request, int page, string pageParameter, CairnOptions options)
        => SwapQueryParam(request, pageParameter, page.ToString(CultureInfo.InvariantCulture), options);

    /// <summary>The current request URL with a single query parameter set to <paramref name="value"/>.</summary>
    public static string SwapQueryParam(HttpRequest request, string name, string value, CairnOptions options)
    {
        var query = new Dictionary<string, StringValues>(QueryHelpers.ParseQuery(request.QueryString.Value))
        {
            [name] = value,
        };

        return QueryHelpers.AddQueryString(BaseUrl(request, options), query);
    }

    private static string CurrentUrl(HttpRequest request, CairnOptions options)
        => $"{BaseUrl(request, options)}{request.QueryString}";

    // The request URL up to the path, honoring the configured URL style: path-relative, rebased onto the
    // public origin (its path replaces the request's PathBase, mirroring LinkGenerator's pathBase), or the
    // incoming request's own scheme://host.
    private static string BaseUrl(HttpRequest request, CairnOptions options)
        => options.UrlStyle == LinkUrlStyle.PathRelative
            ? $"{request.PathBase}{request.Path}"
            : options.PublicBaseUri is { } publicBase
                ? $"{publicBase.Scheme}://{publicBase.Authority}{LinkGeneratorUrlResolver.BasePath(publicBase)}{request.Path}"
                : $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
}
