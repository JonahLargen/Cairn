using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Cairn.AspNetCore.Internal;

/// <summary>Builds pagination links for offset and cursor paged resources.</summary>
internal static class PaginationLinks
{
    /// <summary>Offset links: self/first/prev/next/last derived from the page number.</summary>
    public static IReadOnlyDictionary<string, HalLink> BuildOffset(HttpContext http, IPagedResource paged, Func<int, string> pageUrl, CairnOptions options)
    {
        HalLink Page(int page) => new(Transform(http, options, pageUrl(page)));

        var links = new Dictionary<string, HalLink>
        {
            ["self"] = Page(paged.Page),
        };

        if (paged.TotalPages > 0)
        {
            links["first"] = Page(1);
            links["last"] = Page(paged.TotalPages);
        }

        // Clamp prev to the last valid page so an over-range request (Page > TotalPages) still points back
        // into range rather than to a non-existent page.
        if (paged.Page > 1 && paged.TotalPages > 0)
        {
            links["prev"] = Page(Math.Min(paged.Page - 1, paged.TotalPages));
        }

        if (paged.Page < paged.TotalPages)
        {
            links["next"] = Page(paged.Page + 1);
        }

        return links;
    }

    /// <summary>Cursor links: self is the current URL; next/prev come from the app-supplied cursors.</summary>
    public static IReadOnlyDictionary<string, HalLink> BuildCursor(HttpContext http, ICursorPagedResource cursor, Func<string, string> cursorUrl, CairnOptions options)
    {
        var links = new Dictionary<string, HalLink>
        {
            ["self"] = new(Transform(http, options, CurrentUrl(http.Request, options))),
        };

        if (cursor.Next is { Length: > 0 } next)
        {
            links["next"] = new(Transform(http, options, cursorUrl(next)));
        }

        if (cursor.Prev is { Length: > 0 } prev)
        {
            links["prev"] = new(Transform(http, options, cursorUrl(prev)));
        }

        return links;
    }

    /// <summary>The default page URL: the current request URL with <paramref name="pageParameter"/> set to the page number.</summary>
    public static string DefaultPageUrl(HttpRequest request, int page, string pageParameter, CairnOptions options)
        => SwapQueryParam(request, pageParameter, page.ToString(CultureInfo.InvariantCulture), options);

    /// <summary>
    /// The current request URL with a single query parameter set to <paramref name="value"/>. The parameter is
    /// matched case-insensitively (ASP.NET Core binds query keys case-insensitively), and an incoming key's
    /// original casing is preserved in the rewritten URL.
    /// </summary>
    public static string SwapQueryParam(HttpRequest request, string name, string value, CairnOptions options)
    {
        // ParseQuery already folds keys case-insensitively; copying with the same comparer makes the indexer
        // replace an existing "Page" (keeping its casing) instead of appending a second "page".
        var query = new Dictionary<string, StringValues>(QueryHelpers.ParseQuery(request.QueryString.Value), StringComparer.OrdinalIgnoreCase)
        {
            [name] = value,
        };

        return QueryHelpers.AddQueryString(BaseUrl(request, options), query);
    }

    private static string CurrentUrl(HttpRequest request, CairnOptions options)
        => $"{BaseUrl(request, options)}{request.QueryString}";

    // Post-process a pagination URL through CairnOptions.TransformUrl (a no-op when it is unset), so route
    // links, affordances, explicit hrefs, and pagination links all pass through the same host rewrite hook.
    private static string Transform(HttpContext http, CairnOptions options, string url)
        => options.TransformUrl is { } transform ? transform(http, url) : url;

    // The request URL up to the path, honoring the configured URL style: path-relative, rebased onto the
    // public origin (its path replaces the request's PathBase, mirroring LinkGenerator's pathBase), or the
    // incoming request's own scheme://host. The public origin resolves per request, so multi-tenant hosts
    // can rebase pagination links onto the same tenant origin as their route links.
    private static string BaseUrl(HttpRequest request, CairnOptions options)
        => options.UrlStyle == LinkUrlStyle.PathRelative
            ? $"{request.PathBase}{request.Path}"
            : options.PublicBaseUriFor(request.HttpContext) is { } publicBase
                ? $"{publicBase.Scheme}://{publicBase.Authority}{LinkGeneratorUrlResolver.BasePath(publicBase)}{request.Path}"
                : $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
}
