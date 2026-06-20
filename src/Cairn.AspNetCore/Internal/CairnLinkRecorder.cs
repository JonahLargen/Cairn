using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Compute stage: builds the hypermedia for an endpoint's returned value — offset and cursor envelopes
/// get pagination links and each element of a collection is linked — recording it by reference for the
/// emit stage to serialize.
/// </summary>
internal static class CairnLinkRecorder
{
    public static async ValueTask RecordAsync(HttpContext http, object? result)
    {
        if (Unwrap(result) is not IValueHttpResult { Value: { } value })
        {
            return;
        }

        var services = http.RequestServices;
        var engine = services.GetRequiredService<ILinkEngine>();
        var options = services.GetRequiredService<CairnOptions>();
        var context = new LinkContext(
            services.GetRequiredService<ILinkUrlResolver>(),
            services.GetRequiredService<ILinkAuthorizer>(),
            options.Mode);

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        await RecordAsync(http, value, engine, context, options, visited);
    }

    private static async ValueTask RecordAsync(HttpContext http, object? value, ILinkEngine engine, LinkContext context, CairnOptions options, HashSet<object> visited)
    {
        if (value is null)
        {
            return;
        }

        // Offset envelope (IPagedResource or AddPaging): page-number links, then link each item.
        IPagedResource? offset = value as IPagedResource;
        if (offset is null && options.TryGetPagedView(value, out var pagedView))
        {
            offset = pagedView;
        }

        if (offset is { } paged)
        {
            await RecordEnvelopeAsync(http, value, paged.Items, () => PaginationLinks.BuildOffset(paged, ResolvePageUrl(http, options)), engine, context, options, visited);
            return;
        }

        // Cursor envelope (ICursorPagedResource or AddCursorPaging): cursor links, then link each item.
        ICursorPagedResource? cursor = value as ICursorPagedResource;
        if (cursor is null && options.TryGetCursorView(value, out var cursorView))
        {
            cursor = cursorView;
        }

        if (cursor is { } cursored)
        {
            await RecordEnvelopeAsync(http, value, cursored.Items, () => PaginationLinks.BuildCursor(http.Request, cursored, ResolveCursorUrl(http, options)), engine, context, options, visited);
            return;
        }

        // Plain collection: each element is linked; the array/list itself carries no _links.
        if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                await RecordAsync(http, item, engine, context, options, visited);
            }

            return;
        }

        if (!visited.Add(value))
        {
            return;
        }

        var linkSet = await engine.BuildAsync(value, context, http.RequestAborted);
        if (!linkSet.IsEmpty)
        {
            CairnLinkStore.Record(http, value, ToPayload(linkSet));
        }
    }

    // Shared: record the envelope's navigation links, then link each of its items.
    private static async ValueTask RecordEnvelopeAsync(HttpContext http, object envelope, IEnumerable items, Func<IReadOnlyDictionary<string, HalLink>> buildLinks, ILinkEngine engine, LinkContext context, CairnOptions options, HashSet<object> visited)
    {
        if (visited.Add(envelope))
        {
            CairnLinkStore.Record(http, envelope, new ResourceHypermedia(buildLinks(), null));
        }

        foreach (var item in items)
        {
            await RecordAsync(http, item, engine, context, options, visited);
        }
    }

    // Per-route .WithPageLinks() wins over the global PageLink, which wins over the default query-swap.
    private static Func<int, string> ResolvePageUrl(HttpContext http, CairnOptions options)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<PageLinkMetadata>() is { } perRoute)
        {
            return page => perRoute.PageLink(http.Request, page);
        }

        if (options.PageLink is { } global)
        {
            return page => global(http.Request, page);
        }

        return page => PaginationLinks.DefaultPageUrl(http.Request, page, options.PageQueryParameter);
    }

    // Per-route .WithCursorLinks() wins over the global CursorLink, which wins over the default query-swap.
    private static Func<string, string> ResolveCursorUrl(HttpContext http, CairnOptions options)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<CursorLinkMetadata>() is { } perRoute)
        {
            return cursor => perRoute.CursorLink(http.Request, cursor);
        }

        if (options.CursorLink is { } global)
        {
            return cursor => global(http.Request, cursor);
        }

        return cursor => PaginationLinks.SwapQueryParam(http.Request, options.CursorQueryParameter, cursor);
    }

    // Peel Results<T1,T2,...> unions down to the concrete result that carries the value.
    private static object? Unwrap(object? result)
    {
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        return result;
    }

    private static ResourceHypermedia ToPayload(LinkSet linkSet)
    {
        IReadOnlyDictionary<string, HalLink>? links = linkSet.Links.Count == 0
            ? null
            : linkSet.Links.ToDictionary(
                l => l.Relation.Value,
                l => new HalLink(l.Href) { Title = l.Title, Templated = l.Templated ? true : null });

        IReadOnlyDictionary<string, HalAction>? actions = linkSet.Affordances.Count == 0
            ? null
            : linkSet.Affordances.ToDictionary(
                a => a.Name.Value,
                a => new HalAction(a.Href, a.Method) { Title = a.Title });

        return new ResourceHypermedia(links, actions);
    }
}
