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

        if (paged.Page > 1)
        {
            links["prev"] = new(pageUrl(paged.Page - 1));
        }

        if (paged.Page < paged.TotalPages)
        {
            links["next"] = new(pageUrl(paged.Page + 1));
        }

        return links;
    }

    /// <summary>Cursor links: self is the current URL; next/prev come from the app-supplied cursors.</summary>
    public static IReadOnlyDictionary<string, HalLink> BuildCursor(HttpRequest request, ICursorPagedResource cursor, Func<string, string> cursorUrl)
    {
        var links = new Dictionary<string, HalLink>
        {
            ["self"] = new(CurrentUrl(request)),
        };

        if (cursor.Next is { Length: > 0 } next)
        {
            links["next"] = new(cursorUrl(next));
        }

        if (cursor.Previous is { Length: > 0 } previous)
        {
            links["prev"] = new(cursorUrl(previous));
        }

        return links;
    }

    /// <summary>The default page URL: the current request URL with <paramref name="pageParameter"/> set to the page number.</summary>
    public static string DefaultPageUrl(HttpRequest request, int page, string pageParameter)
        => SwapQueryParam(request, pageParameter, page.ToString(CultureInfo.InvariantCulture));

    /// <summary>The current request URL with a single query parameter set to <paramref name="value"/>.</summary>
    public static string SwapQueryParam(HttpRequest request, string name, string value)
    {
        var query = new Dictionary<string, StringValues>(QueryHelpers.ParseQuery(request.QueryString.Value))
        {
            [name] = value,
        };

        var baseUri = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
        return QueryHelpers.AddQueryString(baseUri, query);
    }

    private static string CurrentUrl(HttpRequest request)
        => $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
}
