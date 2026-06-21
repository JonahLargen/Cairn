using System.Collections;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Compute stage: resolves the request's hypermedia format and builds the hypermedia for an endpoint's
/// returned value — offset and cursor envelopes get pagination links and each element of a collection is
/// linked — recording it by reference for the emit stage to serialize.
/// </summary>
internal static class CairnLinkRecorder
{
    private static readonly ConcurrentDictionary<Type, byte> WarnedHalActionTypes = new();
    private static readonly ConcurrentDictionary<Type, byte> WarnedValueTypes = new();

    // Minimal-API entry point: peel the IResult to its value, then record it.
    public static ValueTask RecordResultAsync(HttpContext http, object? result)
        => Unwrap(result) is IValueHttpResult { Value: { } value } ? RecordValueAsync(http, value) : ValueTask.CompletedTask;

    // MVC entry point: the resource value (e.g. ObjectResult.Value) is recorded directly.
    public static async ValueTask RecordValueAsync(HttpContext http, object? value)
    {
        if (value is null)
        {
            return;
        }

        var services = http.RequestServices;
        var options = services.GetRequiredService<CairnOptions>();

        var format = ResolveFormat(http, options);
        CairnLinkStore.SetFormat(http, format);

        var scope = new RecordScope(
            services.GetRequiredService<ILinkEngine>(),
            new LinkContext(
                services.GetRequiredService<ILinkUrlResolver>(),
                services.GetRequiredService<ILinkAuthorizer>(),
                options.Mode,
                services,
                http.RequestAborted),
            options,
            format,
            services.GetService<ILoggerFactory>()?.CreateLogger("Cairn.AspNetCore"),
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        await RecordAsync(http, value, scope);

        // Only relabel the response media type when the top-level value itself is a decorated resource object
        // (a configured single resource, or a paged/cursor envelope). A problem document or uncovered body keeps
        // its content type per RFC 9457; and a bare collection serializes as a JSON array, which is not a HAL
        // document even though its elements carry _links — so it stays application/json.
        if (CairnLinkStore.Lookup(http, value) is not null)
        {
            ApplyContentType(http, format);
        }
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
            WarnIfValueType(scope, value);
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

    // Pick the highest-quality acceptable hypermedia type, honoring q-values (RFC 9110): a q=0 excludes a type,
    // and a higher-q plain application/json/wildcard wins over hal/hal-forms. Returns null to use the default.
    private static HypermediaFormat? NegotiateFromAccept(HttpRequest request)
    {
        if (request.Headers.Accept.Count == 0)
        {
            return null;
        }

        var winner = HypermediaFormat.Default;
        var bestQuality = 0.0;

        foreach (var media in MediaTypeHeaderValue.ParseList(request.Headers.Accept))
        {
            var quality = media.Quality ?? 1.0;
            if (quality <= 0.0 || FormatFor(media.MediaType) is not { } format || quality <= bestQuality)
            {
                continue;
            }

            bestQuality = quality;
            winner = format;
        }

        return winner == HypermediaFormat.Default ? null : winner;
    }

    private static HypermediaFormat? FormatFor(Microsoft.Extensions.Primitives.StringSegment mediaType)
    {
        if (mediaType.Equals("application/prs.hal-forms+json", StringComparison.OrdinalIgnoreCase))
        {
            return HypermediaFormat.HalForms;
        }

        if (mediaType.Equals("application/hal+json", StringComparison.OrdinalIgnoreCase))
        {
            return HypermediaFormat.Hal;
        }

        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/*+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/*", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("*/*", StringComparison.OrdinalIgnoreCase))
        {
            return HypermediaFormat.Default;
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
            var (response, target) = ((HttpResponse, string))state;

            // Swap only the media type, keeping any parameters (a media-type API version `v`, a charset, ...),
            // and only for plain application/json — problem+json and explicit vendor types are left untouched.
            if (response.ContentType is { } existing
                && MediaTypeHeaderValue.TryParse(existing, out var parsed)
                && parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                parsed.MediaType = target;
                response.ContentType = parsed.ToString();
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

    // Hypermedia is correlated by reference between the compute and emit stages; a boxed value type is a
    // different instance at each stage, so its links can never be emitted. Warn once rather than fail silently.
    private static void WarnIfValueType(RecordScope scope, object value)
    {
        if (!value.GetType().IsValueType || scope.Logger is not { } logger)
        {
            return;
        }

        if (WarnedValueTypes.TryAdd(value.GetType(), 0))
        {
            logger.LogWarning(
                "Cairn: hypermedia cannot be attached to value type {ResourceType} (links are correlated by reference); use a class or record. No links will be emitted for it.",
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

    // A repeated relation overwrites: the last declaration wins. This keeps a duplicate rel from crashing
    // serialization (the wire model is keyed by relation) rather than throwing on a configuration mistake.
    private static ResourceHypermedia ToPayload(LinkSet linkSet)
    {
        Dictionary<string, HalLink>? links = null;
        if (linkSet.Links.Count > 0)
        {
            links = new Dictionary<string, HalLink>(StringComparer.Ordinal);
            foreach (var link in linkSet.Links)
            {
                links[link.Relation.Value] = new HalLink(link.Href) { Title = link.Title, Templated = link.Templated ? true : null, Type = link.Type };
            }
        }

        Dictionary<string, HalAction>? actions = null;
        if (linkSet.Affordances.Count > 0)
        {
            actions = new Dictionary<string, HalAction>(StringComparer.Ordinal);
            foreach (var affordance in linkSet.Affordances)
            {
                actions[affordance.Name.Value] = new HalAction(affordance.Href, affordance.Method) { Title = affordance.Title, Input = affordance.Input };
            }
        }

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
