using System.Collections;

namespace Cairn.AspNetCore.Internal;

/// <summary>Adapts a <see cref="PagedView"/> (from an <c>AddPaging</c> selector) to <see cref="IPagedResource"/>.</summary>
internal sealed class PagedViewAdapter(PagedView view) : IPagedResource
{
    public IEnumerable Items => view.Items;

    public int Page => view.Page;

    public int PageSize => view.PageSize;

    public int TotalCount => view.TotalCount;

    public int TotalPages => view.PageSize <= 0 ? 0 : (view.TotalCount + view.PageSize - 1) / view.PageSize;
}
