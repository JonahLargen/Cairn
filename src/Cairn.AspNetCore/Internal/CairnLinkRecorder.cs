using System.Collections;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Compute stage: resolves the request's hypermedia format and builds the hypermedia for an endpoint's
/// returned value — offset and cursor envelopes get pagination links and each element of a collection is
/// linked — recording it by reference for the emit stage to serialize.
/// </summary>
internal static class CairnLinkRecorder
{
    private static readonly ConcurrentDictionary<Type, byte> WarnedHalActionTypes = new();

    public static async ValueTask RecordAsync(HttpContext http, object? result)
    {
        if (Unwrap(result) is not IValueHttpResult { Value: { } value })
        {
            return;
        }

        var services = http.RequestServices;
        var options = services.GetRequiredService<CairnOptions>();

        var format = ResolveFormat(http, options);
        CairnLinkStore.SetFormat(http, format);
        ApplyContentType(http, format);

        var scope = new RecordScope(
            services.GetRequiredService<ILinkEngine>(),
            new LinkContext(services.GetRequiredService<ILinkUrlResolver>(), services.GetRequiredService<ILinkAuthorizer>(), options.Mode),
            options,
            format,
            services.GetService<ILoggerFactory>()?.CreateLogger("Cairn.AspNetCore"),
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        await RecordAsync(http, value, scope);
    }

    private static async ValueTask RecordAsync(HttpContext http, object? value, RecordScope scope)
    {
        if (value is null)
        {
            return;
        }

        // Offset envelope (IPagedResource or AddPaging): page-number links, then link each item.
        IPagedResource? offset = value as IPagedResource;
        if (offset is null && scope.Options.TryGetPagedView(value, out var pagedView))
        {
            offset = pagedView;
        }

        if (offset is { } paged)
        {
            await RecordEnvelopeAsync(http, value, paged.Items, () => PaginationLinks.BuildOffset(paged, ResolvePageUrl(http, scope.Options)), scope);
            return;
        }

        // Cursor envelope (ICursorPagedResource or AddCursorPaging): cursor links, then link each item.
        ICursorPagedResource? cursor = value as ICursorPagedResource;
        if (cursor is null && scope.Options.TryGetCursorView(value, out var cursorView))
        {
            cursor = cursorView;
        }

        if (cursor is { } cursored)
        {
            await RecordEnvelopeAsync(http, value, cursored.Items, () => PaginationLinks.BuildCursor(http.Request, cursored, ResolveCursorUrl(http, scope.Options)), scope);
            return;
        }

        // Plain collection: each element is linked; the array/list itself carries no _links.
        if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                await RecordAsync(http, item, scope);
            }

            return;
        }

        if (!scope.Visited.Add(value))
        {
            return;
        }

        var linkSet = await scope.Engine.BuildAsync(value, scope.Context, http.RequestAborted);
        WarnIfActionsBlocked(scope, value, linkSet);

        if (!linkSet.IsEmpty)
        {
            CairnLinkStore.Record(http, value, ToPayload(linkSet));
        }
    }

    // Shared: record the envelope's navigation links, then link each of its items.
    private static async ValueTask RecordEnvelopeAsync(HttpContext http, object envelope, IEnumerable items, Func<IReadOnlyDictionary<string, HalLink>> buildLinks, RecordScope scope)
    {
        if (scope.Visited.Add(envelope))
        {
            CairnLinkStore.Record(http, envelope, new ResourceHypermedia(buildLinks(), null));
        }

        foreach (var item in items)
        {
            await RecordAsync(http, item, scope);
        }
    }

    // Per-endpoint .WithHypermediaFormat() forces a format; otherwise the Accept header may negotiate one;
    // otherwise the global default applies.
    private static HypermediaFormat ResolveFormat(HttpContext http, CairnOptions options)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<HypermediaFormatMetadata>() is { } forced)
        {
            return forced.Format;
        }

        if (options.NegotiateFormat && NegotiateFromAccept(http.Request) is { } negotiated)
        {
            return negotiated;
        }

        return options.DefaultFormat;
    }

    private static HypermediaFormat? NegotiateFromAccept(HttpRequest request)
    {
        foreach (var accept in request.Headers.Accept)
        {
            if (accept is null)
            {
                continue;
            }

            if (accept.Contains("application/prs.hal-forms+json", StringComparison.OrdinalIgnoreCase))
            {
                return HypermediaFormat.HalForms;
            }

            if (accept.Contains("application/hal+json", StringComparison.OrdinalIgnoreCase))
            {
                return HypermediaFormat.Hal;
            }
        }

        return null;
    }

    private static void ApplyContentType(HttpContext http, HypermediaFormat format)
    {
        var mediaType = format switch
        {
            HypermediaFormat.Hal => "application/hal+json",
            HypermediaFormat.HalForms => "application/prs.hal-forms+json",
            _ => null,
        };

        if (mediaType is null)
        {
            return;
        }

        http.Response.OnStarting(static state =>
        {
            var (response, contentType) = ((HttpResponse, string))state;
            if (response.ContentType is { } existing && existing.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType = contentType;
            }

            return Task.CompletedTask;
        }, (http.Response, mediaType));
    }

    private static void WarnIfActionsBlocked(RecordScope scope, object value, LinkSet linkSet)
    {
        if (scope.Format != HypermediaFormat.Hal || linkSet.Affordances.Count == 0 || scope.Logger is not { } logger)
        {
            return;
        }

        if (WarnedHalActionTypes.TryAdd(value.GetType(), 0))
        {
            logger.LogWarning(
                "Cairn: affordances on {ResourceType} are not emitted in HAL format (HAL has no actions). Use HAL-FORMS to include them.",
                value.GetType().Name);
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

    private sealed record RecordScope(
        ILinkEngine Engine,
        LinkContext Context,
        CairnOptions Options,
        HypermediaFormat Format,
        ILogger? Logger,
        HashSet<object> Visited);
}
