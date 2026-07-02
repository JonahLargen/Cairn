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
    private const string EmitDiagnosticKey = "Cairn.EmitDiagnostic";

    private static readonly ConcurrentDictionary<Type, byte> WarnedHalActionTypes = new();
    private static readonly ConcurrentDictionary<Type, byte> WarnedValueTypes = new();
    private static readonly ConcurrentDictionary<Type, byte> WarnedAsyncEnumerableTypes = new();
    private static readonly ConcurrentDictionary<Type, byte> WarnedUnconfiguredTypes = new();
    private static readonly ConcurrentDictionary<Type, byte> WarnedUnemittedTypes = new();

    // Minimal-API entry point: a handler may return an IResult carrying the value, or the bare value itself.
    // Returns the (possibly substituted) result the endpoint filter should pass down the pipeline.
    public static async ValueTask<object?> RecordResultAsync(HttpContext http, object? result)
    {
        switch (Unwrap(result))
        {
            // TypedResults.Ok(...) and friends: record the carried value. The result instance is immutable,
            // so a deferred sequence cannot be swapped for its buffer here; if its re-enumeration yields new
            // instances, the emit-stage diagnostic reports the miss.
            case IValueHttpResult { Value: { } value }:
                await RecordValueAsync(http, value);
                return result;

            // Any other IResult carries no value; a bare string serializes as text/plain — nothing to link.
            case IResult or string or null:
                return result;

            // Bare return: the handler returned the DTO (or sequence) itself. Record it, handing forward the
            // buffered copy of a deferred sequence so links stay correlated by reference.
            case var value:
                return await RecordValueAsync(http, value);
        }
    }

    // MVC entry point: the resource value (e.g. ObjectResult.Value) is recorded directly. Returns the value
    // to serialize — the buffered copy when the input was a deferred sequence, otherwise the input itself.
    public static async ValueTask<object> RecordValueAsync(HttpContext http, object value)
    {
        var services = http.RequestServices;
        var options = services.GetRequiredService<CairnOptions>();

        var format = ResolveFormat(http, options);
        CairnLinkStore.SetFormat(http, format);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Cairn.AspNetCore");

        // An async stream is fundamentally incompatible with the two-pass design: links are computed before
        // serialization, and the stream cannot be enumerated twice. Warn once rather than fail silently.
        if (AsyncEnumerableElementType(value) is { } elementType)
        {
            WarnAsyncEnumerable(logger, elementType);
            return value;
        }

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
            logger,
            services.GetService<ILinkConfigProvider>(),
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        // A deferred sequence (LINQ query, IQueryable, iterator) would be enumerated here and again by the
        // serializer — running any underlying query twice and, if the second pass yields new instances,
        // losing every link. Materialize it once and hand the buffer forward instead.
        if (value is IEnumerable enumerable and not string
            && value is not IPagedResource and not ICursorPagedResource
            && !options.IsPagingEnvelope(value.GetType())
            && !IsMaterialized(enumerable))
        {
            value = BufferSequence(enumerable);
        }

        await RecordAsync(http, value, scope, warnIfUnconfigured: true);

        // Only relabel the response media type when the top-level value itself is a decorated resource object
        // (a configured single resource, or a paged/cursor envelope). A problem document or uncovered body keeps
        // its content type per RFC 9457; and a bare collection serializes as a JSON array, which is not a HAL
        // document even though its elements carry _links — so it stays application/json.
        if (CairnLinkStore.Has(http, value))
        {
            ApplyContentType(http, format);
        }

        RegisterEmitMissDiagnostic(http, logger);
        return value;
    }

    private static async ValueTask RecordAsync(HttpContext http, object? value, RecordScope scope, bool warnIfUnconfigured)
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
                await RecordAsync(http, item, scope, warnIfUnconfigured);
            }

            return;
        }

        if (!scope.Visited.Add(value))
        {
            return;
        }

        var linkSet = await scope.Engine.BuildAsync(value, scope.Context, http.RequestAborted);
        WarnIfActionsBlocked(scope, value, linkSet);

        if (linkSet.IsEmpty)
        {
            if (warnIfUnconfigured)
            {
                WarnIfUnconfigured(scope, value);
            }

            return;
        }

        WarnIfValueType(scope, value);

        var embedded = await RecordEmbeddedAsync(http, linkSet, scope);
        CairnLinkStore.Record(http, value, ToPayload(linkSet, embedded, scope.Options.Curies));
    }

    // Record each embedded child first (recursing so it gets its own _links), then capture the map.
    private static async ValueTask<IReadOnlyDictionary<string, object>?> RecordEmbeddedAsync(HttpContext http, LinkSet linkSet, RecordScope scope)
    {
        if (linkSet.Embedded.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var group in linkSet.Embedded)
        {
            foreach (var child in group.Resources)
            {
                // An embedded child without its own config is a normal shape — no unconfigured warning.
                await RecordAsync(http, child, scope, warnIfUnconfigured: false);
            }

            map[group.Relation.Value] = group.Single ? group.Resources[0] : group.Resources;
        }

        return map;
    }

    // Shared: record the envelope's navigation links merged with any hypermedia configured for the envelope
    // type itself (a create affordance, a templated search link, ...), then link each of its items. On a
    // relation collision the pagination link wins — it carries the paging state for this request.
    private static async ValueTask RecordEnvelopeAsync(HttpContext http, object envelope, IEnumerable items, Func<IReadOnlyDictionary<string, HalLink>> buildLinks, RecordScope scope)
    {
        if (scope.Visited.Add(envelope))
        {
            var linkSet = await scope.Engine.BuildAsync(envelope, scope.Context, http.RequestAborted);
            WarnIfActionsBlocked(scope, envelope, linkSet);
            var embedded = await RecordEmbeddedAsync(http, linkSet, scope);
            var configured = ToPayload(linkSet, embedded, scope.Options.Curies);

            var links = new Dictionary<string, HalLinkValue>(StringComparer.Ordinal);
            if (configured.Links is not null)
            {
                foreach (var (relation, link) in configured.Links)
                {
                    links[relation] = link;
                }
            }

            foreach (var (relation, link) in buildLinks())
            {
                links[relation] = new HalLinkValue([link]);
            }

            CairnLinkStore.Record(http, envelope, new ResourceHypermedia(links, configured.Actions, configured.Embedded));
        }

        foreach (var item in items)
        {
            await RecordAsync(http, item, scope, warnIfUnconfigured: true);
        }
    }

    // Per-endpoint .WithHypermediaFormat() forces a format; otherwise the Accept header may negotiate one;
    // otherwise the global default applies. A negotiable response varies by Accept — advertise that so
    // shared caches (CDNs, OutputCache) don't replay one client's shape to another (RFC 9110 §12.5.5).
    private static HypermediaFormat ResolveFormat(HttpContext http, CairnOptions options)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<HypermediaFormatMetadata>() is { } forced)
        {
            return forced.Format;
        }

        if (options.NegotiateFormat)
        {
            AddVaryAccept(http.Response);
            if (NegotiateFromAccept(http.Request) is { } negotiated)
            {
                return negotiated;
            }
        }

        return options.DefaultFormat;
    }

    private static void AddVaryAccept(HttpResponse response)
    {
        foreach (var existing in response.Headers.Vary)
        {
            if (existing is not null && existing.Contains("Accept", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var field in existing.Split(','))
                {
                    if (field.Trim().Equals("Accept", StringComparison.OrdinalIgnoreCase) || field.Trim() == "*")
                    {
                        return;
                    }
                }
            }
        }

        response.Headers.Append(HeaderNames.Vary, "Accept");
    }

    // Pick the highest-quality acceptable hypermedia type, honoring q-values (RFC 9110): a q=0 excludes a type,
    // and a higher-q plain application/json or wildcard wins over hal/hal-forms. An explicit application/json
    // ask negotiates the plain format even when DefaultFormat is hal — the client did not accept a hal media
    // type. Only a winning wildcard (or no Accept, or one that can't be parsed) returns null to use the default.
    private static HypermediaFormat? NegotiateFromAccept(HttpRequest request)
    {
        if (request.Headers.Accept.Count == 0
            || !MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var accepted))
        {
            return null;
        }

        NegotiatedFormat? winner = null;
        var bestQuality = 0.0;

        foreach (var media in accepted)
        {
            var quality = media.Quality ?? 1.0;
            if (quality <= 0.0 || FormatFor(media.MediaType) is not { } format || quality <= bestQuality)
            {
                continue;
            }

            bestQuality = quality;
            winner = format;
        }

        return winner switch
        {
            NegotiatedFormat.Hal => HypermediaFormat.Hal,
            NegotiatedFormat.HalForms => HypermediaFormat.HalForms,
            NegotiatedFormat.PlainJson => HypermediaFormat.Default,
            _ => null,
        };
    }

    private enum NegotiatedFormat
    {
        Hal,
        HalForms,

        // Explicit application/json: the client asked for plain JSON, not merely "anything".
        PlainJson,

        // A wildcard accepts every format, so it expresses no preference — the configured default applies.
        AnyFormat,
    }

    private static NegotiatedFormat? FormatFor(Microsoft.Extensions.Primitives.StringSegment mediaType)
    {
        if (mediaType.Equals("application/prs.hal-forms+json", StringComparison.OrdinalIgnoreCase))
        {
            return NegotiatedFormat.HalForms;
        }

        if (mediaType.Equals("application/hal+json", StringComparison.OrdinalIgnoreCase))
        {
            return NegotiatedFormat.Hal;
        }

        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return NegotiatedFormat.PlainJson;
        }

        // application/*+json matches the hal media types too, so it is a wildcard here.
        if (mediaType.Equals("application/*+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/*", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("*/*", StringComparison.OrdinalIgnoreCase))
        {
            return NegotiatedFormat.AnyFormat;
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

    private static void WarnAsyncEnumerable(ILogger? logger, Type elementType)
    {
        if (logger is not null && WarnedAsyncEnumerableTypes.TryAdd(elementType, 0))
        {
            logger.LogWarning(
                "Cairn: hypermedia cannot be attached to an IAsyncEnumerable<{ResourceType}> response (links are computed before serialization, and an async stream cannot be enumerated twice). Materialize it first (e.g. ToListAsync()). No links will be emitted for it.",
                elementType.Name);
        }
    }

    // A response value whose runtime type (and base classes) has no registered config yields no links at all.
    // On an endpoint that explicitly opted in via WithLinks()/[CairnLinks], that is far more likely a missing
    // registration than intent — warn once per type instead of no-oping silently.
    private static void WarnIfUnconfigured(RecordScope scope, object value)
    {
        var type = value.GetType();
        if (value is string or Microsoft.AspNetCore.Mvc.ProblemDetails
            || type.IsValueType
            || scope.Logger is not { } logger
            || scope.Configs is null
            || scope.Configs.GetConfig(type) is not null)
        {
            return;
        }

        if (WarnedUnconfiguredTypes.TryAdd(type, 0))
        {
            logger.LogWarning(
                "Cairn: no link configuration is registered for {ResourceType} or any of its base types; it will serialize without hypermedia. Register a LinkConfig<{ResourceType}> (AddLinks / AddLinksFromAssembly), or remove WithLinks()/[CairnLinks] if this is intended.",
                type.Name,
                type.Name);
        }
    }

    // After the response completes, report any recorded hypermedia the serializer never asked for — the
    // reference correlation between compute and emit broke (typically a deferred sequence inside an immutable
    // result, re-enumerated into fresh instances). Without this, the links just vanish with no trace.
    private static void RegisterEmitMissDiagnostic(HttpContext http, ILogger? logger)
    {
        if (logger is null || !CairnLinkStore.HasEntries(http) || !http.Items.TryAdd(EmitDiagnosticKey, true))
        {
            return;
        }

        http.Response.OnCompleted(() =>
        {
            // An error response may legitimately skip serializing the recorded value.
            if (http.Response.StatusCode < 400)
            {
                foreach (var type in CairnLinkStore.UnemittedTypes(http))
                {
                    if (WarnedUnemittedTypes.TryAdd(type, 0))
                    {
                        logger.LogWarning(
                            "Cairn: hypermedia was computed for {ResourceType} but never emitted. Links are correlated by reference between compute and serialization; this usually means a deferred sequence (LINQ projection, IQueryable) produced different instances when serialized. Materialize the sequence (e.g. ToList()) before returning it.",
                            type.Name);
                    }
                }
            }

            return Task.CompletedTask;
        });
    }

    private static Type? AsyncEnumerableElementType(object value)
    {
        foreach (var iface in value.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }

        return null;
    }

    // Materialized collections (arrays, lists, sets, ...) expose a count; a deferred sequence (LINQ query,
    // IQueryable, iterator) does not.
    private static bool IsMaterialized(IEnumerable value)
    {
        if (value is ICollection)
        {
            return true;
        }

        foreach (var iface in value.GetType().GetInterfaces())
        {
            if (iface.IsGenericType
                && iface.GetGenericTypeDefinition() is var definition
                && (definition == typeof(ICollection<>) || definition == typeof(IReadOnlyCollection<>)))
            {
                return true;
            }
        }

        return false;
    }

    // Enumerate the deferred sequence exactly once into a List<T> (preserving the element type so the
    // serialization contract is unchanged) that both the recorder and the serializer share.
    private static IEnumerable BufferSequence(IEnumerable source)
    {
        Type? elementType = null;
        foreach (var iface in source.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                break;
            }
        }

        var buffer = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType ?? typeof(object)))!;
        foreach (var item in source)
        {
            buffer.Add(item);
        }

        return buffer;
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

    // Links are grouped by relation: a single link for a rel emits as a HAL link object, several as a HAL link
    // array. Insertion order (the declaration order) is preserved within a relation.
    private static ResourceHypermedia ToPayload(LinkSet linkSet, IReadOnlyDictionary<string, object>? embedded, IReadOnlyDictionary<string, string> curies)
    {
        Dictionary<string, HalLinkValue>? links = null;
        if (linkSet.Links.Count > 0)
        {
            var grouped = new Dictionary<string, List<HalLink>>(StringComparer.Ordinal);
            foreach (var link in linkSet.Links)
            {
                if (!grouped.TryGetValue(link.Relation.Value, out var list))
                {
                    list = [];
                    grouped[link.Relation.Value] = list;
                }

                list.Add(new HalLink(link.Href)
                {
                    Name = link.Name,
                    Title = link.Title,
                    Templated = link.Templated ? true : null,
                    Type = link.Type,
                    Deprecation = link.Deprecation,
                    Hreflang = link.Hreflang,
                    Profile = link.Profile,
                });
            }

            links = new Dictionary<string, HalLinkValue>(StringComparer.Ordinal);
            foreach (var (relation, list) in grouped)
            {
                links[relation] = new HalLinkValue(list);
            }

            AddCuries(links, curies);
        }

        Dictionary<string, HalAction>? actions = null;
        if (linkSet.Affordances.Count > 0)
        {
            actions = new Dictionary<string, HalAction>(StringComparer.Ordinal);
            foreach (var affordance in linkSet.Affordances)
            {
                actions[affordance.Name.Value] = new HalAction(affordance.Href, affordance.Method) { Title = affordance.Title, Input = affordance.Input, ContentType = affordance.ContentType };
            }
        }

        return new ResourceHypermedia(links, actions, embedded);
    }

    // Surface a curies array for every registered prefix actually used by a relation (e.g. "acme:widget").
    private static void AddCuries(Dictionary<string, HalLinkValue> links, IReadOnlyDictionary<string, string> curies)
    {
        if (curies.Count == 0)
        {
            return;
        }

        var used = new List<HalLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in links.Keys)
        {
            var colon = relation.IndexOf(':');
            if (colon > 0 && curies.TryGetValue(relation.Substring(0, colon), out var href) && seen.Add(relation.Substring(0, colon)))
            {
                used.Add(new HalLink(href) { Name = relation.Substring(0, colon), Templated = true });
            }
        }

        if (used.Count > 0)
        {
            links["curies"] = new HalLinkValue(used, alwaysArray: true);
        }
    }

    private sealed record RecordScope(
        ILinkEngine Engine,
        LinkContext Context,
        CairnOptions Options,
        HypermediaFormat Format,
        ILogger? Logger,
        ILinkConfigProvider? Configs,
        HashSet<object> Visited);
}
