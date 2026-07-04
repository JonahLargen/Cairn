using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
            // so a deferred sequence cannot be swapped for its buffer here — computing links enumerates it
            // once and serialization enumerates it again. Warn up front (an IQueryable runs its query twice,
            // and fresh instances lose every link) instead of only diagnosing after the response.
            case IValueHttpResult { Value: { } value }:
                WarnIfDeferredInImmutableResult(http, value);
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
            value = BufferOrKeep(enumerable);
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
        WarnIfPolicyGatedUnderOutputCaching(http, scope, value);

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
        CairnLinkStore.Record(http, value, ToPayload(linkSet, embedded, scope));
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
        items = MaterializeEnvelopeItems(envelope, items, scope);

        if (scope.Visited.Add(envelope))
        {
            var linkSet = await scope.Engine.BuildAsync(envelope, scope.Context, http.RequestAborted);
            WarnIfActionsBlocked(scope, envelope, linkSet);
            WarnIfPolicyGatedUnderOutputCaching(http, scope, envelope);
            var embedded = await RecordEmbeddedAsync(http, linkSet, scope);
            var configured = ToPayload(linkSet, embedded, scope);

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
    // HTTP caches (CDNs, proxies, the ResponseCaching middleware) don't replay one client's shape to
    // another (RFC 9110 §12.5.5). ASP.NET Core's OutputCache is NOT covered by this header: it ignores the
    // response's Vary and only splits on what its policy names, so output-cached negotiable endpoints need
    // CacheOutput(p => p.SetVaryByHeader("Accept")) — see docs/articles/caching.md.
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

    // Pick the most acceptable hypermedia type per RFC 9110 §12.5.1: for every format the server can emit,
    // the most specific matching Accept range determines its quality (exact type > application/*+json >
    // application/* > */*), q=0 excludes it, and the highest-quality survivor wins. Quality ties break on
    // specificity ("*/*, application/hal+json" asks for hal), then on server preference — the configured
    // default first, so a bare wildcard expresses no preference. Returns null (use the default) when there is
    // no Accept header, it can't be parsed, or no emittable format is acceptable.
    private static (HypermediaFormat, IHypermediaFormatter?)? NegotiateFromAccept(HttpRequest request, CairnOptions options)
    {
        if (request.Headers.Accept.Count == 0
            || !MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var accepted))
        {
            return null;
        }

        (NegotiatedFormat? Builtin, IHypermediaFormatter? Custom)? winner = null;
        var bestQuality = 0.0;
        var bestSpecificity = 0;

        foreach (var (builtin, custom, mediaType) in Candidates(options))
        {
            var (quality, specificity) = BestMatch(accepted, mediaType);
            if (specificity == 0 || quality <= 0.0)
            {
                continue;   // no range matched, or the most specific match excluded it with q=0
            }

            if (quality > bestQuality || (quality == bestQuality && specificity > bestSpecificity))
            {
                winner = (builtin, custom);
                bestQuality = quality;
                bestSpecificity = specificity;
            }
        }

        if (winner is not { } selected)
        {
            return null;
        }

        if (selected.Custom is not null)
        {
            return (HypermediaFormat.Default, selected.Custom);
        }

        return selected.Builtin switch
        {
            NegotiatedFormat.Hal => (HypermediaFormat.Hal, null),
            NegotiatedFormat.HalForms => (HypermediaFormat.HalForms, null),
            _ => (HypermediaFormat.Default, null),
        };
    }

    // Every format the server can emit, in tie-break order: the configured default first (a wildcard-only
    // match expresses no preference, so the default should win it), then registered custom formatters, then
    // the remaining built-ins.
    private static IEnumerable<(NegotiatedFormat? Builtin, IHypermediaFormatter? Custom, string MediaType)> Candidates(CairnOptions options)
    {
        var preferred = options.DefaultFormat switch
        {
            HypermediaFormat.Hal => NegotiatedFormat.Hal,
            HypermediaFormat.HalForms => NegotiatedFormat.HalForms,
            _ => NegotiatedFormat.PlainJson,
        };

        yield return (preferred, null, MediaTypeOf(preferred));

        foreach (var formatter in options.Formatters)
        {
            yield return (null, formatter, formatter.MediaType);
        }

        foreach (var format in (NegotiatedFormat[])[NegotiatedFormat.Hal, NegotiatedFormat.PlainJson, NegotiatedFormat.HalForms])
        {
            if (format != preferred)
            {
                yield return (format, null, MediaTypeOf(format));
            }
        }
    }

    private static string MediaTypeOf(NegotiatedFormat format) => format switch
    {
        NegotiatedFormat.Hal => "application/hal+json",
        NegotiatedFormat.HalForms => "application/prs.hal-forms+json",
        _ => "application/json",
    };

    // The most specific matching range determines the candidate's quality (RFC 9110 §12.5.1); among ranges of
    // equal specificity the first one counts. Specificity 0 means no range matched at all.
    private static (double Quality, int Specificity) BestMatch(IList<MediaTypeHeaderValue> accepted, string candidate)
    {
        var quality = 0.0;
        var specificity = 0;

        foreach (var media in accepted)
        {
            var rank = Specificity(media.MediaType, candidate);
            if (rank > specificity)
            {
                specificity = rank;
                quality = media.Quality ?? 1.0;
            }
        }

        return (quality, specificity);
    }

    // 4: exact media type; 3: a suffix range like application/*+json (plain application/json is outside it);
    // 2: a type range like application/*; 1: */*; 0: no match.
    private static int Specificity(Microsoft.Extensions.Primitives.StringSegment rangeSegment, string candidate)
    {
        if (!rangeSegment.HasValue)
        {
            return 0;
        }

        var range = rangeSegment.Value;
        if (string.Equals(range, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (range == "*/*")
        {
            return 1;
        }

        // Beyond exact and */*, only "type/*" and "type/*+suffix" ranges can match, and only when the
        // candidate has the same type (compare through the '/' so type lengths must agree).
        var slash = range.IndexOf('/');
        if (slash < 0
            || slash + 1 >= range.Length
            || range[slash + 1] != '*'
            || candidate.Length <= slash
            || candidate[slash] != '/'
            || string.Compare(range, 0, candidate, 0, slash + 1, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return 0;
        }

        if (range.Length == slash + 2)
        {
            return 2;   // type/*
        }

        // type/*+suffix: the candidate's subtype must carry the suffix (and be more than the bare suffix).
        var suffix = range[(slash + 2)..];
        return suffix.StartsWith('+')
            && candidate.Length > slash + 1 + suffix.Length
            && candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? 3
            : 0;
    }

    private enum NegotiatedFormat
    {
        Hal,
        HalForms,

        // Explicit application/json: the client asked for plain JSON, not merely "anything".
        PlainJson,
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

    // A link config that gates links or affordances behind authorization policies produces per-caller
    // hypermedia. OutputCache stores one response per policy-defined key — it ignores the response's Vary
    // header entirely — so on an output-cached endpoint one caller's link set is replayed to every other
    // caller (showing actions they can't invoke, or hiding ones they could). Warn once per resource type;
    // the config referencing a policy at all is the signal, whether or not this caller passed the gate.
    private static void WarnIfPolicyGatedUnderOutputCaching(HttpContext http, RecordScope scope, object value)
    {
        if (scope.Logger is not { } logger
            || http.Features.Get<Microsoft.AspNetCore.OutputCaching.IOutputCacheFeature>() is null
            || scope.Configs?.GetConfig(value.GetType()) is not Cairn.Internal.IPolicyReportingConfig reporting
            || reporting.Policies.Count == 0)
        {
            return;
        }

        if (scope.WarnOnce.Mark("output-cache-policy", value.GetType()))
        {
            logger.LogWarning(
                "Cairn: the link configuration for {ResourceType} gates hypermedia behind authorization policies, and this response is subject to output caching (IOutputCacheFeature is present). OutputCache ignores the Vary header, so one caller's policy-dependent links would be replayed to other callers from the shared cache. Vary the cache by caller, or don't output-cache endpoints returning {ResourceType}. See the caching documentation.",
                value.GetType().Name,
                value.GetType().Name);
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

    // A deferred sequence (LINQ query, IQueryable, iterator) inside an immutable result cannot be buffered
    // into it: the compute pass enumerates it once and the serializer enumerates it again. That is a double
    // query for an IQueryable, and when re-enumeration yields new instances every link is lost too. Warn
    // while the request is still running — the post-response emit-miss diagnostic only fires for the cases
    // that actually lose links.
    private static void WarnIfDeferredInImmutableResult(HttpContext http, object value)
    {
        if (value is not IEnumerable enumerable
            || value is string or IPagedResource or ICursorPagedResource
            || IsMaterialized(enumerable))
        {
            return;
        }

        var services = http.RequestServices;
        if (services.GetRequiredService<CairnOptions>().IsPagingEnvelope(value.GetType()))
        {
            return;   // an envelope's deferred items are buffered back through its items property instead
        }

        var elementType = ElementTypeOf(enumerable);
        if (services.GetService<ILoggerFactory>()?.CreateLogger("Cairn.AspNetCore") is { } logger
            && services.GetRequiredService<WarnOnce>().Mark("deferred-result", elementType))
        {
            logger.LogWarning(
                "Cairn: the response is a deferred sequence of {ResourceType} inside an immutable result (e.g. TypedResults.Ok(query)). Computing hypermedia enumerates it once and serialization enumerates it again — an IQueryable runs its query twice — and if re-enumeration yields new instances the links are lost. Materialize it first (e.g. ToList()/ToListAsync()).",
                elementType.Name);
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
                        // Name the actual cause. A non-object contract (a custom JsonConverter handles the type)
                        // can never receive the injected properties; an object contract with none of the
                        // injected properties means the contract was built before the type was recognized as
                        // hypermedia-capable; otherwise the contract could emit but the recorded instance never
                        // reached serialization (deferred re-enumeration).
                        if (HasNonObjectContract(http, type))
                        {
                            logger.LogWarning(
                                "Cairn: hypermedia was computed for {ResourceType} but cannot be emitted: the type serializes through a custom JsonConverter, so its JSON contract is not an object contract and Cairn's property injection never runs. Remove the converter from the resource type (or emit the hypermedia from the converter yourself).",
                                type.Name);
                        }
                        else if (ContractMissingHypermedia(http, type))
                        {
                            logger.LogWarning(
                                "Cairn: hypermedia was computed for {ResourceType} but never emitted: its serializer contract carries none of Cairn's injected hypermedia properties, so the emit stage had nothing to write into. The JSON contract was built and cached before a link config covering {ResourceType} (its own or a subtype's) was registered — register every link config during AddCairn, before the first request is served.",
                                type.Name,
                                type.Name);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Cairn: hypermedia was computed for {ResourceType} but never emitted. Links are correlated by reference between compute and serialization; this usually means a deferred sequence (LINQ projection, IQueryable) produced different instances when serialized. Materialize the sequence (e.g. ToList()) before returning it.",
                                type.Name);
                        }
                    }
                }
            }

            return Task.CompletedTask;
        });
    }

    // Whether the serializer handles this type through something other than an object contract — i.e. a
    // custom JsonConverter — in which case Cairn's contract-modifier injection never sees it. Checks both
    // serializer option sets since either pipeline (minimal APIs, MVC) may have produced the response.
    private static bool HasNonObjectContract(HttpContext http, Type type)
    {
        foreach (var serializer in SerializerOptionSets(http))
        {
            if (serializer is not null
                && serializer.TryGetTypeInfo(type, out var contract)
                && contract.Kind != JsonTypeInfoKind.Object)
            {
                return true;
            }
        }

        return false;
    }

    // Whether the serializer built an object contract for this type that carries none of Cairn's injected
    // hypermedia properties (keyed on the always-present "_links"). That means the contract was finalized
    // before the type was recognized as hypermedia-capable — typically a link config registered after the
    // type was first serialized — so the emit stage had no property to write the recorded links into.
    private static bool ContractMissingHypermedia(HttpContext http, Type type)
    {
        foreach (var serializer in SerializerOptionSets(http))
        {
            if (serializer is null || !serializer.TryGetTypeInfo(type, out var contract) || contract.Kind != JsonTypeInfoKind.Object)
            {
                continue;
            }

            foreach (var property in contract.Properties)
            {
                if (string.Equals(property.Name, "_links", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    // Both serializer option sets that could have produced the response: minimal APIs (and WriteAsJsonAsync)
    // read Http.Json options; MVC controllers read Mvc.Json options. Either may be absent.
    private static IEnumerable<JsonSerializerOptions?> SerializerOptionSets(HttpContext http)
    {
        var services = http.RequestServices;
        yield return services.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions;
        yield return services.GetService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()?.Value.JsonSerializerOptions;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Detects IAsyncEnumerable<T> to warn that async streams cannot carry hypermedia. The interface is preserved on any type the app itself consumes as an async stream; a miss just skips the warning for a value that could not be streamed anyway.")]
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
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Detects ICollection<T>/IReadOnlyCollection<T> to tell materialized collections from deferred sequences. These BCL interfaces are preserved on collection types the app itself uses; a miss treats the value as deferred, causing at most a redundant buffering pass.")]
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
    // serialization contract is unchanged) that both the recorder and the serializer share. Returns the
    // source unchanged when the List<T> instantiation is unavailable (Native AOT with an uninstantiated
    // value-type element), leaving the sequence deferred — the documented degradation.
    [ExcludeFromCodeCoverage(Justification = "The catch arm requires a Native AOT runtime missing the List<T> instantiation; it cannot execute on the JIT test host.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "List<T> over reference-type elements uses shared generic code that always exists under Native AOT; a missing value-type instantiation throws NotSupportedException, which degrades to keeping the sequence deferred.")]
    private static IEnumerable BufferOrKeep(IEnumerable source)
    {
        IList buffer;
        try
        {
            buffer = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ElementTypeOf(source)))!;
        }
        catch (NotSupportedException)
        {
            return source;
        }

        foreach (var item in source)
        {
            buffer.Add(item);
        }

        return buffer;
    }

    // The List<T> buffer type for the sequence's element type, or null when the instantiation is
    // unavailable under Native AOT (the caller falls back to its logged warning). The return annotation
    // lets the caller instantiate the buffer without re-running MakeGenericType.
    [ExcludeFromCodeCoverage(Justification = "The catch arm requires a Native AOT runtime missing the List<T> instantiation; it cannot execute on the JIT test host.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "A missing List<T> instantiation under Native AOT throws NotSupportedException, which the caller turns into the documented logged fallback.")]
    [UnconditionalSuppressMessage("Trimming", "IL2073:UnrecognizedReflectionPattern",
        Justification = "Every List<T> instantiation has a public parameterless constructor.")]
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    private static Type? TryMakeBufferType(IEnumerable items)
    {
        try
        {
            return typeof(List<>).MakeGenericType(ElementTypeOf(items));
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Finds IEnumerable<T> to preserve the buffered list's element type. The interface is preserved on sequence types the app itself enumerates; a miss falls back to object elements, which serialize by their runtime contracts.")]
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
    // produced it, so the compute and emit stages share the same instances. Only a true set accessor is used:
    // an init-only property is a declared immutability contract that reflection must not break (and a shared
    // envelope instance must not be rewritten behind it). When no settable property exposes the sequence, the
    // items are left as-is; that risks the double enumeration and correlation break, so warn now rather than
    // relying only on the post-response emit-miss diagnostic.
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Searches the envelope's public properties for the one exposing the deferred items so the buffer can be written back. A property removed by trimming simply isn't found, taking the same logged fallback as an envelope with no settable items property.")]
    private static IEnumerable MaterializeEnvelopeItems(object envelope, IEnumerable items, RecordScope scope)
    {
        if (items is string || IsMaterialized(items))
        {
            return items;
        }

        // A null buffer type (Native AOT without the List<T> instantiation) falls through to the warning.
        if (TryMakeBufferType(items) is { } bufferType)
        {
            foreach (var property in envelope.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod is not { } setter
                    || IsInitOnly(setter)
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

                var buffer = (IList)Activator.CreateInstance(bufferType)!;
                foreach (var item in items)
                {
                    buffer.Add(item);
                }

                property.SetValue(envelope, buffer);
                return buffer;
            }
        }

        if (scope.Logger is { } logger && scope.WarnOnce.Mark("deferred-envelope", envelope.GetType()))
        {
            logger.LogWarning(
                "Cairn: envelope {ResourceType} exposes its items as a deferred sequence with no settable property to buffer them back into (the property is init-only, computed, or non-public). The sequence is enumerated once to compute links and again to serialize, and if re-enumeration yields new instances the item links are lost. Materialize the items (e.g. ToList()) before constructing the envelope.",
                envelope.GetType().Name);
        }

        return items;
    }

    // An init accessor compiles as a setter whose return parameter carries the IsExternalInit modreq.
    private static bool IsInitOnly(MethodInfo setter)
        => setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));

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
    private static ResourceHypermedia ToPayload(LinkSet linkSet, IReadOnlyDictionary<string, object>? embedded, RecordScope scope)
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

        links = AddCuries(links, actions, embedded, scope);
        return new ResourceHypermedia(links, actions, embedded);
    }

    // Surface a curies array for every registered prefix actually used by a rel-keyed section — _links,
    // affordances (_actions/_templates), or _embedded — so a curie'd relation anywhere in the document has
    // its documentation link resolvable. The array always lives in _links per HAL. A HAL response never
    // emits affordances, so a prefix used only by an affordance name would advertise a curie no relation in
    // the document carries — skip the affordance section there.
    private static VerbatimKeyDictionary<HalLinkValue>? AddCuries(
        VerbatimKeyDictionary<HalLinkValue>? links,
        IReadOnlyDictionary<string, HalAction>? actions,
        IReadOnlyDictionary<string, object>? embedded,
        RecordScope scope)
    {
        var curies = scope.Options.Curies;
        if (curies.Count == 0)
        {
            return links;
        }

        // A response can carry many linked items whose rels use no curie prefix at all; defer the List/HashSet
        // until a rel actually resolves to a registered prefix, so the common no-prefix path allocates nothing.
        List<HalLink>? used = null;
        HashSet<string>? seen = null;
        CollectCuries(links?.Keys, curies, ref used, ref seen);
        if (scope.Format != HypermediaFormat.Hal)
        {
            CollectCuries(EmittedActionNames(actions, scope.Format), curies, ref used, ref seen);
        }

        CollectCuries(embedded?.Keys, curies, ref used, ref seen);

        if (used is null)
        {
            return links;
        }

        links ??= new VerbatimKeyDictionary<HalLinkValue>(StringComparer.OrdinalIgnoreCase);
        links["curies"] = new HalLinkValue(used, alwaysArray: true);
        return links;
    }

    // The affordance names that actually appear as keys on the wire. In HAL-FORMS, an affordance emitted
    // under the reserved "default" template key — marked AsDefault(), or the response's sole template — never
    // shows its own name in the document, so a curie advertised for its prefix would document a relation the
    // document doesn't carry. Cairn's own _actions shape (and custom formatters' documents) keep the names.
    private static IEnumerable<string>? EmittedActionNames(IReadOnlyDictionary<string, HalAction>? actions, HypermediaFormat format)
    {
        if (actions is null || format != HypermediaFormat.HalForms)
        {
            return actions?.Keys;
        }

        if (actions.Count == 1)
        {
            return null;   // the sole template is keyed "default" regardless of AsDefault()
        }

        List<string>? names = null;
        foreach (var (name, action) in actions)
        {
            if (!action.IsDefault)
            {
                (names ??= []).Add(name);
            }
        }

        return names;
    }

    private static void CollectCuries(IEnumerable<string>? relations, IReadOnlyDictionary<string, string> curies, ref List<HalLink>? used, ref HashSet<string>? seen)
    {
        if (relations is null)
        {
            return;
        }

        foreach (var relation in relations)
        {
            var colon = relation.IndexOf(':');
            if (colon > 0 && curies.TryGetValue(relation[..colon], out var href)
                && (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(relation[..colon]))
            {
                (used ??= []).Add(new HalLink(href) { Name = relation[..colon], Templated = true });
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
