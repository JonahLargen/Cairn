using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>How Cairn renders the URLs of route-resolved links and pagination links.</summary>
public enum LinkUrlStyle
{
    /// <summary>Absolute URLs derived from the incoming request's scheme and host (or <see cref="CairnOptions.PublicBaseUri"/> when set).</summary>
    Absolute,

    /// <summary>Path-relative URLs (<c>/orders/1</c>) — immune to proxy/host misconfiguration; clients resolve them against the document's base.</summary>
    PathRelative,
}

/// <summary>Configures Cairn's hypermedia services.</summary>
public sealed class CairnOptions
{
    private readonly Dictionary<Type, Func<object, IPagedResource>> _paging = [];
    private readonly Dictionary<Type, Func<object, ICursorPagedResource>> _cursorPaging = [];
    // Curie prefixes are rel prefixes, and rels compare case-insensitively (RFC 8288).
    private readonly Dictionary<string, string> _curies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IHypermediaFormatter> _formatters = [];
    private Uri? _publicBaseUri;
    private bool _frozen;

    internal LinkConfigRegistry Registry { get; } = new();

    internal IReadOnlyDictionary<string, string> Curies => _curies;

    internal IReadOnlyList<IHypermediaFormatter> Formatters => _formatters;

    /// <summary>How unresolved link targets are handled (default <see cref="LinkResolutionMode.Lax"/>).</summary>
    public LinkResolutionMode Mode { get; set; } = LinkResolutionMode.Lax;

    /// <summary>
    /// The wire format used when the request doesn't negotiate one (default <see cref="HypermediaFormat.Default"/>).
    /// Set to <see cref="HypermediaFormat.None"/> to make hypermedia opt-in by the client: an un-negotiated
    /// request (plain <c>application/json</c>, a wildcard, or no <c>Accept</c> header) then serializes the bare
    /// resource, and links appear only when the caller's <c>Accept</c> header names a hypermedia media type.
    /// </summary>
    public HypermediaFormat DefaultFormat { get; set; } = HypermediaFormat.Default;

    /// <summary>How link URLs are rendered (default <see cref="LinkUrlStyle.Absolute"/>).</summary>
    public LinkUrlStyle UrlStyle { get; set; } = LinkUrlStyle.Absolute;

    /// <summary>
    /// The public origin absolute links are generated against — scheme, host, and optional path base — instead
    /// of the incoming request's. Set this when the app runs behind a proxy or gateway whose forwarded headers
    /// aren't (or can't be) configured, so links never leak internal hostnames. Ignored when
    /// <see cref="UrlStyle"/> is <see cref="LinkUrlStyle.PathRelative"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is a relative URI.</exception>
    public Uri? PublicBaseUri
    {
        get => _publicBaseUri;
        set
        {
            if (value is { IsAbsoluteUri: false })
            {
                throw new ArgumentException("PublicBaseUri must be an absolute URI (e.g. https://api.example.com).", nameof(value));
            }

            _publicBaseUri = value;
        }
    }

    /// <summary>
    /// A per-request resolver for the public origin absolute links are built against — scheme, host, and
    /// optional path base — for multi-tenant hosts that serve several origins from one application. It is
    /// evaluated against the current request's <see cref="HttpContext"/> and takes precedence over
    /// <see cref="PublicBaseUri"/>; returning <see langword="null"/> falls back to <see cref="PublicBaseUri"/>
    /// (then the incoming request's own origin), so a resolver can rebase only the tenants it recognizes. The
    /// URI it returns must be absolute. Keep it cheap and free of side effects — it may be consulted several
    /// times while a response's links are built. Ignored when <see cref="UrlStyle"/> is <see cref="LinkUrlStyle.PathRelative"/>.
    /// </summary>
    public Func<HttpContext, Uri?>? ResolvePublicBaseUri { get; set; }

    // The effective public origin for this request: the per-request resolver wins when it yields a URI,
    // then the static PublicBaseUri. Null means neither is configured, so links fall back to the request's
    // own scheme/host. A relative URI from the resolver can't name an origin and would fail with an opaque
    // error deeper in link generation, so reject it up front with the same guidance as the setter.
    internal Uri? PublicBaseUriFor(HttpContext http)
    {
        if (ResolvePublicBaseUri is { } resolve && resolve(http) is { } resolved)
        {
            if (!resolved.IsAbsoluteUri)
            {
                throw new InvalidOperationException(
                    "Cairn: ResolvePublicBaseUri must return an absolute URI (e.g. https://tenant.example.com) or null.");
            }

            return resolved;
        }

        return _publicBaseUri;
    }

    /// <summary>Whether a known hypermedia media type in the request's <c>Accept</c> header selects the format (default <see langword="true"/>).</summary>
    public bool NegotiateFormat { get; set; } = true;

    /// <summary>
    /// The media types Cairn negotiates its wire formats by and labels responses with — the plain
    /// <c>application/json</c>, the flat-shape vendor type, HAL, and HAL-FORMS tokens. Override any of them to
    /// match your API's media-type scheme; they are validated (concrete and mutually distinct) when the host starts.
    /// </summary>
    public CairnMediaTypeOptions MediaTypes { get; } = new();

    /// <summary>
    /// Whether the authorization policies referenced by link configurations are validated against the host's
    /// <c>IAuthorizationPolicyProvider</c> at startup (default <see langword="true"/>). Disable this when the
    /// host uses a dynamic policy provider that materializes policies only after boot (e.g. from a database or
    /// per-tenant store), where a startup lookup would report registered-later policies as unknown.
    /// </summary>
    public bool ValidateAuthorizationPolicies { get; set; } = true;

    /// <summary>The query string parameter swapped by the default offset pagination links (default <c>page</c>).</summary>
    public string PageQueryParameter { get; set; } = "page";

    /// <summary>The query string parameter swapped by the default cursor pagination links (default <c>cursor</c>).</summary>
    public string CursorQueryParameter { get; set; } = "cursor";

    /// <summary>
    /// App-wide builder for a page number's URL, derived from the request (override per route with
    /// <c>WithPageLinks</c>). When unset, offset pagination links swap <see cref="PageQueryParameter"/> on the
    /// current request URL.
    /// </summary>
    public Func<HttpRequest, int, string>? PageLink { get; set; }

    /// <summary>
    /// App-wide builder for a cursor's URL, derived from the request (override per route with
    /// <c>WithCursorLinks</c>). When unset, cursor pagination links swap <see cref="CursorQueryParameter"/> on
    /// the current request URL.
    /// </summary>
    public Func<HttpRequest, string, string>? CursorLink { get; set; }

    /// <summary>
    /// Post-processes each route-resolved link and affordance URL (the current request and the generated URL).
    /// Use it to carry request state onto links — e.g. re-apply a query-string API version so links stay on the
    /// current version. Pagination links already preserve other query parameters, so they aren't passed here.
    /// </summary>
    public Func<HttpContext, string, string>? TransformUrl { get; set; }

    // Structural registrations feed caches built at (or shortly after) startup — JSON contracts carry the
    // injected hypermedia properties only for types configured when the contract is first built, negotiation
    // snapshots the formatter list, and startup validation reads the registry once. A registration that lands
    // after the options singleton is resolved would take effect partially or not at all, so it fails loudly
    // instead (the pre-freeze path is the ordinary AddCairn(configure) / Configure<CairnOptions> flow).
    internal void Freeze()
    {
        ValidateMediaTypes();
        _frozen = true;
    }

    // The negotiation candidate set (the four built-in tokens plus every custom formatter) must have distinct
    // media types, or an Accept range could match two formats and the winner would be arbitrary. Checked once,
    // when the options freeze at startup, so a collision fails the host boot rather than a request.
    private void ValidateMediaTypes()
    {
        var tokens = MediaTypes.All;
        for (var i = 0; i < tokens.Count; i++)
        {
            for (var j = i + 1; j < tokens.Count; j++)
            {
                if (string.Equals(tokens[i].Value, tokens[j].Value, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Cairn: MediaTypes.{tokens[i].Name} and MediaTypes.{tokens[j].Name} are both '{tokens[i].Value}'. Each wire format needs a distinct media type so Accept negotiation is unambiguous.");
                }
            }
        }

        foreach (var formatter in _formatters)
        {
            foreach (var (name, value) in tokens)
            {
                if (string.Equals(formatter.MediaType, value, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Cairn: the custom formatter for '{formatter.MediaType}' collides with the built-in MediaTypes.{name}. Give the formatter a different media type, or override MediaTypes.{name}.");
                }
            }
        }
    }

    private void ThrowIfFrozen(string operation)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"Cairn: {operation} was called after CairnOptions was resolved from the container. " +
                "Configuration registered this late is silently ignored by caches built at startup (JSON contracts, format negotiation, policy validation), " +
                "so it is rejected instead. Register all link configs, formatters, curies, and paging envelopes during AddCairn(...) or via Configure<CairnOptions> before the host starts.");
        }
    }

    /// <summary>Registers the link configuration for resources of type <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">Called after the options were resolved (configuration is frozen once the host starts).</exception>
    public CairnOptions AddLinks<T>(LinkConfig<T> config)
    {
        ThrowIfFrozen(nameof(AddLinks));
        Registry.Add(config);
        return this;
    }

    /// <summary>Discovers and registers every non-abstract <see cref="LinkConfig{T}"/> with a public parameterless constructor in <paramref name="assembly"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    [RequiresUnreferencedCode("Assembly scanning enumerates and instantiates types via reflection, which trimming may remove. Register configs explicitly with AddLinks<T> in trimmed applications.")]
    [RequiresDynamicCode("Discovered configs are compiled through MakeGenericType over their runtime resource types. Register configs explicitly with AddLinks<T> in Native AOT applications.")]
    public CairnOptions AddLinksFromAssembly(Assembly assembly)
    {
        ThrowIfFrozen(nameof(AddLinksFromAssembly));
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes())
        {
            if (type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                && IsLinkConfig(type)
                && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                Registry.Add(Activator.CreateInstance(type)!);
            }
        }

        return this;
    }

    /// <summary>Discovers and registers every <see cref="LinkConfig{T}"/> in the assembly that contains <typeparamref name="T"/>.</summary>
    [RequiresUnreferencedCode("Assembly scanning enumerates and instantiates types via reflection, which trimming may remove. Register configs explicitly with AddLinks<T> in trimmed applications.")]
    [RequiresDynamicCode("Discovered configs are compiled through MakeGenericType over their runtime resource types. Register configs explicitly with AddLinks<T> in Native AOT applications.")]
    public CairnOptions AddLinksFromAssemblyContaining<T>() => AddLinksFromAssembly(typeof(T).Assembly);

    private static bool IsLinkConfig(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(LinkConfig<>))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Registers a custom hypermedia wire format. Its media type participates in <c>Accept</c> negotiation
    /// (alongside the built-in formats) and can be forced per endpoint with
    /// <c>WithHypermediaFormat(formatter.MediaType)</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="formatter"/> is null.</exception>
    /// <exception cref="ArgumentException">The formatter declares no media type, an unparseable or wildcard media type, or one is already registered for it.</exception>
    /// <exception cref="InvalidOperationException">Called after the options were resolved (configuration is frozen once the host starts).</exception>
    public CairnOptions AddFormatter(IHypermediaFormatter formatter)
    {
        ThrowIfFrozen(nameof(AddFormatter));
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatter.MediaType, nameof(formatter));

        // The media type is matched exactly during Accept negotiation and emitted as the response
        // Content-Type, so it must be a concrete, parseable type/subtype pair. A malformed or wildcard value
        // would silently never win negotiation (or produce an invalid Content-Type) — reject it up front.
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(formatter.MediaType, out var parsed)
            || parsed.MatchesAllTypes
            || parsed.MatchesAllSubTypes
            || parsed.Parameters.Count > 0)
        {
            throw new ArgumentException(
                $"'{formatter.MediaType}' is not a usable hypermedia media type. Formatters must declare a concrete type/subtype pair without wildcards or parameters (e.g. \"application/vnd.acme+json\"): it is matched exactly during Accept negotiation and written as the response Content-Type.",
                nameof(formatter));
        }

        if (_formatters.Any(existing => string.Equals(existing.MediaType, formatter.MediaType, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A hypermedia formatter for '{formatter.MediaType}' is already registered.", nameof(formatter));
        }

        _formatters.Add(formatter);
        return this;
    }

    /// <summary>
    /// Registers a CURIE: a documentation <paramref name="prefix"/> and a templated <paramref name="hrefTemplate"/>
    /// that must contain the <c>{rel}</c> variable (curies are emitted with <c>templated: true</c>, and clients
    /// expand the relation's suffix into it). When a resource uses a custom relation with the prefix (e.g.
    /// <c>acme:widget</c>), Cairn surfaces the matching curie in <c>_links.curies</c> so clients can resolve
    /// the relation's documentation.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> or <paramref name="hrefTemplate"/> is null or whitespace, or <paramref name="hrefTemplate"/> does not contain <c>{rel}</c>.</exception>
    public CairnOptions AddCurie(string prefix, string hrefTemplate)
    {
        ThrowIfFrozen(nameof(AddCurie));
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(hrefTemplate);
        if (!hrefTemplate.Contains("{rel}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The curie template for prefix '{prefix}' must contain the {{rel}} variable (e.g. \"https://docs.example.com/rels/{{rel}}\"); curies are advertised as templated links that clients expand the relation into.",
                nameof(hrefTemplate));
        }

        _curies[prefix] = hrefTemplate;
        return this;
    }

    /// <summary>
    /// Registers how to read page metadata and items from envelope type <typeparamref name="T"/>, so it
    /// gets offset pagination links without implementing <see cref="IPagedResource"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="describe"/> is null.</exception>
    public CairnOptions AddPaging<T>(Func<T, PagedView> describe)
    {
        ThrowIfFrozen(nameof(AddPaging));
        ArgumentNullException.ThrowIfNull(describe);
        _paging[typeof(T)] = value => new PagedViewAdapter(describe((T)value));
        return this;
    }

    /// <summary>
    /// Registers how to read cursors and items from envelope type <typeparamref name="T"/>, so it gets
    /// cursor pagination links without implementing <see cref="ICursorPagedResource"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="describe"/> is null.</exception>
    public CairnOptions AddCursorPaging<T>(Func<T, CursorView> describe)
    {
        ThrowIfFrozen(nameof(AddCursorPaging));
        ArgumentNullException.ThrowIfNull(describe);
        _cursorPaging[typeof(T)] = value => new CursorPagedViewAdapter(describe((T)value));
        return this;
    }

    internal bool IsPagingEnvelope(Type type) => TryGetEnvelopeShape(type, out _);

    // Envelope lookups honor inheritance like LinkConfigRegistry: a subclass of a registered envelope type is
    // decorated with the same pagination links. The nearest registered ancestor wins, offset before cursor
    // when both are registered for the same type (matching the recorder's evaluation order).
    internal bool TryGetEnvelopeShape(Type type, out bool cursor)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (_paging.ContainsKey(current))
            {
                cursor = false;
                return true;
            }

            if (_cursorPaging.ContainsKey(current))
            {
                cursor = true;
                return true;
            }
        }

        cursor = false;
        return false;
    }

    internal bool TryGetPagedView(object value, out IPagedResource paged)
    {
        for (var current = value.GetType(); current is not null; current = current.BaseType)
        {
            if (_paging.TryGetValue(current, out var factory))
            {
                paged = factory(value);
                return true;
            }

            if (_cursorPaging.ContainsKey(current))
            {
                break;   // the nearest registration says this envelope is cursor-shaped
            }
        }

        paged = null!;
        return false;
    }

    internal bool TryGetCursorView(object value, out ICursorPagedResource cursor)
    {
        for (var current = value.GetType(); current is not null; current = current.BaseType)
        {
            if (_cursorPaging.TryGetValue(current, out var factory))
            {
                cursor = factory(value);
                return true;
            }

            if (_paging.ContainsKey(current))
            {
                break;   // the nearest registration says this envelope is offset-shaped
            }
        }

        cursor = null!;
        return false;
    }
}
