using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>Configures Cairn's hypermedia services.</summary>
public sealed class CairnOptions
{
    private readonly Dictionary<Type, Func<object, IPagedResource>> _paging = [];
    private readonly Dictionary<Type, Func<object, ICursorPagedResource>> _cursorPaging = [];

    internal LinkConfigRegistry Registry { get; } = new();

    /// <summary>How unresolved link targets are handled (default <see cref="LinkResolutionMode.Lax"/>).</summary>
    public LinkResolutionMode Mode { get; set; } = LinkResolutionMode.Lax;

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

    /// <summary>Registers the link configuration for resources of type <typeparamref name="T"/>.</summary>
    public CairnOptions AddLinks<T>(LinkConfig<T> config)
    {
        Registry.Add(config);
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

    internal bool TryGetPagedView(object value, out IPagedResource paged)
    {
        if (_paging.TryGetValue(value.GetType(), out var factory))
        {
            paged = factory(value);
            return true;
        }

        paged = null!;
        return false;
    }

    internal bool TryGetCursorView(object value, out ICursorPagedResource cursor)
    {
        if (_cursorPaging.TryGetValue(value.GetType(), out var factory))
        {
            cursor = factory(value);
            return true;
        }

        cursor = null!;
        return false;
    }
}
