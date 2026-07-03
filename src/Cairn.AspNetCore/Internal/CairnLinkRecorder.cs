using System.Collections;
using System.Reflection;
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

        var (format, custom) = ResolveFormat(http, options);
        CairnLinkStore.SetFormat(http, format);
        CairnLinkStore.SetFormatter(http, custom);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Cairn.AspNetCore");

        // Per-container (registered by AddCairn), so a second host in the same process keeps its own gate.
        var warnOnce = services.GetRequiredService<WarnOnce>();

        using var activity = CairnTelemetry.Source.StartActivity("Cairn.ComputeHypermedia");
        activity?.SetTag("cairn.format", custom?.MediaType ?? format.ToString());
        activity?.SetTag("cairn.resource_type", value.GetType().Name);

        // An async stream is fundamentally incompatible with the two-pass design: links are computed before
        // serialization, and the stream cannot be enumerated twice. Warn once rather than fail silently.
        if (AsyncEnumerableElementType(value) is { } elementType)
        {
            WarnAsyncEnumerable(logger, warnOnce, elementType);
            return value;
        }

        var scope = new RecordScope(
            services.GetRequiredService<ILinkEngine>(),
            new LinkContext(
                services.GetRequiredService<ILinkUrlResolver>(),
                services.GetRequiredService<ILinkAuthorizer>(),
                options.Mode,
                services,
                http.RequestAborted)
            {
                OnUnresolvedLink = unresolved => HandleUnresolved(logger, warnOnce, unresolved),
            },
            options,
            format,
            logger,
            services.GetService<ILinkConfigProvider>(),
            warnOnce,
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
            ApplyContentType(http, format, custom);
        }

        RegisterEmitMissDiagnostic(http, logger, warnOnce);
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
            await RecordEnvelopeAsync(http, value, cursored.Items, () => PaginationLinks.BuildCursor(http.Request, cursored, ResolveCursorUrl(http, scope.Options), scope.Options), scope);
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
        CountComputed(linkSet);
    }

    // Record each embedded child first (recursing so it gets its own _links), then capture the map.
    private static async ValueTask<IReadOnlyDictionary<string, object>?> RecordEmbeddedAsync(HttpContext http, LinkSet linkSet, RecordScope scope)
    {
        if (linkSet.Embedded.Count == 0)
        {
            return null;
        }

        // Rels compare case-insensitively (RFC 8288); keys serialize verbatim regardless of DictionaryKeyPolicy.
        var map = new VerbatimKeyDictionary<object>(StringComparer.OrdinalIgnoreCase);
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
        // The serializer enumerates the envelope's items again after this compute pass, so a deferred
        // sequence must be materialized (and written back onto the envelope) before either pass runs.
        items = MaterializeEnvelopeItems(envelope, items);

        if (scope.Visited.Add(envelope))
        {
            var linkSet = await scope.Engine.BuildAsync(envelope, scope.Context, http.RequestAborted);
            WarnIfActionsBlocked(scope, envelope, linkSet);
            var embedded = await RecordEmbeddedAsync(http, linkSet, scope);
            var configured = ToPayload(linkSet, embedded, scope.Options.Curies);

            var links = new VerbatimKeyDictionary<HalLinkValue>(StringComparer.OrdinalIgnoreCase);
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
            CountComputed(linkSet, extraLinks: links.Count - (configured.Links?.Count ?? 0));
        }

        foreach (var item in items)
        {
            await RecordAsync(http, item, scope, warnIfUnconfigured: true);
        }
    }

    // Per-endpoint .WithHypermediaFormat() forces a format; otherwise the Accept header may negotiate one;
    // otherwise the global default applies. A negotiable response varies by Accept — advertise that so
    // shared caches (CDNs, OutputCache) don't replay one client's shape to another (RFC 9110 §12.5.5).
    // When a custom formatter wins, the built-in format stays Default but the formatter's properties
    // supersede the built-in emission.
    private static (HypermediaFormat Format, IHypermediaFormatter? Custom) ResolveFormat(HttpContext http, CairnOptions options)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<HypermediaFormatMetadata>() is { } forced)
        {
            if (forced.MediaType is not { } mediaType)
            {
                return (forced.Format, null);
            }

            return FindFormatter(options, mediaType)
                ?? throw new InvalidOperationException(
                    $"The endpoint forces the hypermedia format '{mediaType}', but no formatter is registered for it. Register one with CairnOptions.AddFormatter.");
        }

        if (options.NegotiateFormat)
        {
            AddVaryAccept(http.Response);
            if (NegotiateFromAccept(http.Request, options) is { } negotiated)
            {
                return negotiated;
            }
        }

        return (options.DefaultFormat, null);
    }

    // A forced media type resolves to a registered custom formatter, or to the built-in format it names.
    private static (HypermediaFormat, IHypermediaFormatter?)? FindFormatter(CairnOptions options, string mediaType)
    {
        foreach (var formatter in options.Formatters)
        {
            if (string.Equals(formatter.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            {
                return (HypermediaFormat.Default, formatter);
            }
        }

        return FormatFor(mediaType) switch
        {
            NegotiatedFormat.Hal => (HypermediaFormat.Hal, null),
            NegotiatedFormat.HalForms => (HypermediaFormat.HalForms, null),
            NegotiatedFormat.PlainJson => (HypermediaFormat.Default, null),
            _ => null,
        };
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
    // type. Registered custom formatters participate by exact media type. Only a winning wildcard (or no
    // Accept, or one that can't be parsed) returns null to use the default.
    private static (HypermediaFormat, IHypermediaFormatter?)? NegotiateFromAccept(HttpRequest request, CairnOptions options)
    {
        if (request.Headers.Accept.Count == 0
            || !MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var accepted))
        {
            return null;
        }

        NegotiatedFormat? winner = null;
        IHypermediaFormatter? custom = null;
        var bestQuality = 0.0;

        foreach (var media in accepted)
        {
            var quality = media.Quality ?? 1.0;
            if (quality <= 0.0 || quality <= bestQuality)
            {
                continue;
            }

            if (CustomFor(options, media.MediaType) is { } matched)
            {
                bestQuality = quality;
                custom = matched;
                winner = null;
            }
            else if (FormatFor(media.MediaType) is { } format)
            {
                bestQuality = quality;
                winner = format;
                custom = null;
            }
        }

        if (custom is not null)
        {
            return (HypermediaFormat.Default, custom);
        }

        return winner switch
        {
            NegotiatedFormat.Hal => (HypermediaFormat.Hal, null),
            NegotiatedFormat.HalForms => (HypermediaFormat.HalForms, null),
            NegotiatedFormat.PlainJson => (HypermediaFormat.Default, null),
            _ => null,
        };
    }

    private static IHypermediaFormatter? CustomFor(CairnOptions options, Microsoft.Extensions.Primitives.StringSegment mediaType)
    {
        foreach (var formatter in options.Formatters)
        {
            if (mediaType.Equals(formatter.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                return formatter;
            }
        }

        return null;
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

    private static void ApplyContentType(HttpContext http, HypermediaFormat format, IHypermediaFormatter? custom)
    {
        var mediaType = custom?.MediaType ?? format switch
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

    private static void CountComputed(LinkSet linkSet, int extraLinks = 0)
    {
        CairnTelemetry.ResourcesLinked.Add(1);
        if (linkSet.Links.Count + extraLinks > 0)
        {
            CairnTelemetry.LinksComputed.Add(linkSet.Links.Count + extraLinks);
        }

        if (linkSet.Affordances.Count > 0)
        {
            CairnTelemetry.AffordancesComputed.Add(linkSet.Affordances.Count);
        }
    }

    // A lax-mode drop is the silent failure mode: the link just disappears from the payload. Meter every
    // occurrence and log once per (resource type, relation) so production drops are discoverable.
    private static void HandleUnresolved(ILogger? logger, WarnOnce warnOnce, UnresolvedLink unresolved)
    {
        CairnTelemetry.LinksUnresolved.Add(
            1,
            new KeyValuePair<string, object?>("cairn.resource_type", unresolved.ResourceType.Name),
            new KeyValuePair<string, object?>("cairn.relation", unresolved.Relation.Value));

        if (logger is not null && warnOnce.Mark("unresolved-link", $"{unresolved.ResourceType.FullName}|{unresolved.Relation.Value}"))
        {
            logger.LogWarning(
                "Cairn: link '{Relation}' on {ResourceType} targeting {Target} could not be resolved and was dropped (Lax mode). Ensure the endpoint is named (WithName / [Http*(Name=...)]) and all route values are supplied, or use Strict mode to fail instead.",
                unresolved.Relation.Value,
                unresolved.ResourceType.Name,
                unresolved.Target switch
                {
                    RouteLinkTarget route => $"route '{route.RouteName}'",
                    RouteTemplateLinkTarget template => $"route template '{template.RouteName}'",
                    _ => "an explicit URI",
                });
        }
    }

    private static void WarnIfActionsBlocked(RecordScope scope, object value, LinkSet linkSet)
    {
        if (scope.Format != HypermediaFormat.Hal || linkSet.Affordances.Count == 0 || scope.Logger is not { } logger)
        {
            return;
        }

        if (scope.WarnOnce.Mark("hal-actions", value.GetType()))
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

        if (scope.WarnOnce.Mark("value-type", value.GetType()))
        {
            logger.LogWarning(
                "Cairn: hypermedia cannot be attached to value type {ResourceType} (links are correlated by reference); use a class or record. No links will be emitted for it.",
                value.GetType().Name);
        }
    }

    private static void WarnAsyncEnumerable(ILogger? logger, WarnOnce warnOnce, Type elementType)
    {
        if (logger is not null && warnOnce.Mark("async-enumerable", elementType))
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

        if (scope.WarnOnce.Mark("unconfigured", type))
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
    private static void RegisterEmitMissDiagnostic(HttpContext http, ILogger? logger, WarnOnce warnOnce)
    {
        if (logger is null || !CairnLinkStore.HasEntries(http) || !http.Items.TryAdd(EmitDiagnosticKey, true))
        {
            return;
        }

        http.Response.OnCompleted(() =>
        {
            // Only a success response that carries a body should have emitted the hypermedia: an error skips
            // serializing the recorded value, and so do bodiless responses — 204/205 and 3xx (e.g. a healthy
            // conditional GET answered 304 after links were computed).
            var status = http.Response.StatusCode;
            if (status is >= 200 and < 300 and not StatusCodes.Status204NoContent and not StatusCodes.Status205ResetContent)
            {
                foreach (var type in CairnLinkStore.UnemittedTypes(http))
                {
                    CairnTelemetry.HypermediaUnemitted.Add(1, new KeyValuePair<string, object?>("cairn.resource_type", type.Name));
                    if (warnOnce.Mark("unemitted", type))
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
        var buffer = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ElementTypeOf(source)))!;
        foreach (var item in source)
        {
            buffer.Add(item);
        }

        return buffer;
    }

    private static Type ElementTypeOf(IEnumerable source)
    {
        foreach (var iface in source.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }

        return typeof(object);
    }

    // An envelope's deferred items (a LINQ query, IQueryable, iterator) would be enumerated here and again by
    // the serializer — running any underlying query twice and, if the second pass yields new instances, losing
    // every item link. Buffer the sequence once and write the buffer back onto the envelope property that
    // produced it, so the compute and emit stages share the same instances. When no writable property exposes
    // the sequence, it is left as-is and the emit-miss diagnostic reports the correlation break.
    private static IEnumerable MaterializeEnvelopeItems(object envelope, IEnumerable items)
    {
        if (items is string || IsMaterialized(items))
        {
            return items;
        }

        var bufferType = typeof(List<>).MakeGenericType(ElementTypeOf(items));
        foreach (var property in envelope.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is null
                || property.GetIndexParameters().Length != 0
                || !property.PropertyType.IsAssignableFrom(bufferType))
            {
                continue;
            }

            object? current;
            try
            {
                current = property.GetValue(envelope);
            }
            catch
            {
                continue;   // a throwing getter can't be the items source
            }

            if (!ReferenceEquals(current, items))
            {
                continue;
            }

            var buffer = BufferSequence(items);
            property.SetValue(envelope, buffer);
            return buffer;
        }

        return items;
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

        return page => PaginationLinks.DefaultPageUrl(http.Request, page, options.PageQueryParameter, options);
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

        return cursor => PaginationLinks.SwapQueryParam(http.Request, options.CursorQueryParameter, cursor, options);
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
    // array. Rels compare case-insensitively (RFC 8288), keeping the first-declared casing for emission, and
    // insertion order (the declaration order) is preserved within a relation.
    private static ResourceHypermedia ToPayload(LinkSet linkSet, IReadOnlyDictionary<string, object>? embedded, IReadOnlyDictionary<string, string> curies)
    {
        VerbatimKeyDictionary<HalLinkValue>? links = null;
        if (linkSet.Links.Count > 0)
        {
            // Nearly every relation carries a single link, so group lazily: the wire dictionary is built
            // directly and a per-relation list only materializes when a rel actually repeats. This runs per
            // linked resource — a page links every item — so the intermediate grouping dictionary would be
            // pure churn in the common case.
            links = new VerbatimKeyDictionary<HalLinkValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in linkSet.Links)
            {
                var halLink = new HalLink(link.Href)
                {
                    Name = link.Name,
                    Title = link.Title,
                    Templated = link.Templated ? true : null,
                    Type = link.Type,
                    Deprecation = link.Deprecation,
                    Hreflang = link.Hreflang,
                    Profile = link.Profile,
                };

                // The dictionary keeps the first-declared key casing on overwrite, and appending preserves
                // declaration order within the relation.
                if (links.TryGetValue(link.Relation.Value, out var existing))
                {
                    var list = new List<HalLink>(existing.Links.Count + 1);
                    list.AddRange(existing.Links);
                    list.Add(halLink);
                    links[link.Relation.Value] = new HalLinkValue(list);
                }
                else
                {
                    links[link.Relation.Value] = new HalLinkValue([halLink]);
                }
            }
        }

        VerbatimKeyDictionary<HalAction>? actions = null;
        if (linkSet.Affordances.Count > 0)
        {
            actions = new VerbatimKeyDictionary<HalAction>(StringComparer.OrdinalIgnoreCase);
            foreach (var affordance in linkSet.Affordances)
            {
                actions[affordance.Name.Value] = new HalAction(affordance.Href, affordance.Method) { Title = affordance.Title, Input = affordance.Input, ContentType = affordance.ContentType, IsDefault = affordance.IsDefault };
            }
        }

        links = AddCuries(links, actions, embedded, curies);
        return new ResourceHypermedia(links, actions, embedded);
    }

    // Surface a curies array for every registered prefix actually used by a rel-keyed section — _links,
    // affordances (_actions/_templates), or _embedded — so a curie'd relation anywhere in the document has
    // its documentation link resolvable. The array always lives in _links per HAL.
    private static VerbatimKeyDictionary<HalLinkValue>? AddCuries(
        VerbatimKeyDictionary<HalLinkValue>? links,
        IReadOnlyDictionary<string, HalAction>? actions,
        IReadOnlyDictionary<string, object>? embedded,
        IReadOnlyDictionary<string, string> curies)
    {
        if (curies.Count == 0)
        {
            return links;
        }

        var used = new List<HalLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectCuries(links?.Keys, curies, used, seen);
        CollectCuries(actions?.Keys, curies, used, seen);
        CollectCuries(embedded?.Keys, curies, used, seen);

        if (used.Count == 0)
        {
            return links;
        }

        links ??= new VerbatimKeyDictionary<HalLinkValue>(StringComparer.OrdinalIgnoreCase);
        links["curies"] = new HalLinkValue(used, alwaysArray: true);
        return links;
    }

    private static void CollectCuries(IEnumerable<string>? relations, IReadOnlyDictionary<string, string> curies, List<HalLink> used, HashSet<string> seen)
    {
        if (relations is null)
        {
            return;
        }

        foreach (var relation in relations)
        {
            var colon = relation.IndexOf(':');
            if (colon > 0 && curies.TryGetValue(relation[..colon], out var href) && seen.Add(relation[..colon]))
            {
                used.Add(new HalLink(href) { Name = relation[..colon], Templated = true });
            }
        }
    }

    private sealed record RecordScope(
        ILinkEngine Engine,
        LinkContext Context,
        CairnOptions Options,
        HypermediaFormat Format,
        ILogger? Logger,
        ILinkConfigProvider? Configs,
        WarnOnce WarnOnce,
        HashSet<object> Visited);
}
