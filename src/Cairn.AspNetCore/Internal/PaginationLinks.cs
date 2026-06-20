using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Cairn.AspNetCore.Internal;

/// <summary>Builds pagination links for a paged resource from a page-to-URL function.</summary>
internal static class PaginationLinks
{
    public static IReadOnlyDictionary<string, HalLink> Build(IPagedResource paged, Func<int, string> pageUrl)
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

    /// <summary>The default page URL: the current request URL with <paramref name="pageParameter"/> set to the page number.</summary>
    public static string DefaultPageUrl(HttpRequest request, int page, string pageParameter)
    {
        var query = new Dictionary<string, StringValues>(QueryHelpers.ParseQuery(request.QueryString.Value))
        {
            [pageParameter] = page.ToString(CultureInfo.InvariantCulture),
        };

        var baseUri = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
        return QueryHelpers.AddQueryString(baseUri, query);
    }
}
