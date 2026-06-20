using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>Configures Cairn's hypermedia services.</summary>
public sealed class CairnOptions
{
    private readonly Dictionary<Type, Func<object, IPagedResource>> _paging = [];

    internal LinkConfigRegistry Registry { get; } = new();

    /// <summary>How unresolved link targets are handled (default <see cref="LinkResolutionMode.Lax"/>).</summary>
    public LinkResolutionMode Mode { get; set; } = LinkResolutionMode.Lax;

    /// <summary>The query string parameter swapped by the default pagination links (default <c>page</c>).</summary>
    public string PageQueryParameter { get; set; } = "page";

    /// <summary>
    /// App-wide builder for a page number's URL, derived from the request (override per route with
    /// <c>WithPageLinks</c>). When unset, pagination links swap <see cref="PageQueryParameter"/> on the
    /// current request URL.
    /// </summary>
    public Func<HttpRequest, int, string>? PageLink { get; set; }

    /// <summary>Registers the link configuration for resources of type <typeparamref name="T"/>.</summary>
    public CairnOptions AddLinks<T>(LinkConfig<T> config)
    {
        Registry.Add(config);
        return this;
    }

    /// <summary>
    /// Registers how to read page metadata and items from envelope type <typeparamref name="T"/>, so it
    /// gets pagination links without implementing <see cref="IPagedResource"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="describe"/> is null.</exception>
    public CairnOptions AddPaging<T>(Func<T, PagedView> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        _paging[typeof(T)] = value => new PagedViewAdapter(describe((T)value));
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
}
