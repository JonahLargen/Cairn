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

    internal LinkConfigRegistry Registry { get; } = new();

    internal IReadOnlyDictionary<string, string> Curies => _curies;

    internal IReadOnlyList<IHypermediaFormatter> Formatters => _formatters;

    /// <summary>How unresolved link targets are handled (default <see cref="LinkResolutionMode.Lax"/>).</summary>
    public LinkResolutionMode Mode { get; set; } = LinkResolutionMode.Lax;

    /// <summary>The wire format used when the request doesn't negotiate one (default <see cref="HypermediaFormat.Default"/>).</summary>
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

    /// <summary>Whether a known hypermedia media type in the request's <c>Accept</c> header selects the format (default <see langword="true"/>).</summary>
    public bool NegotiateFormat { get; set; } = true;

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

    /// <summary>Registers the link configuration for resources of type <typeparamref name="T"/>.</summary>
    public CairnOptions AddLinks<T>(LinkConfig<T> config)
    {
        Registry.Add(config);
        return this;
    }

    /// <summary>Discovers and registers every non-abstract <see cref="LinkConfig{T}"/> with a public parameterless constructor in <paramref name="assembly"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    public CairnOptions AddLinksFromAssembly(Assembly assembly)
    {
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
    /// <exception cref="ArgumentException">The formatter declares no media type, or one is already registered for it.</exception>
    public CairnOptions AddFormatter(IHypermediaFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatter.MediaType, nameof(formatter));
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
