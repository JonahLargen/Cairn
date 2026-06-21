using System.Collections;

namespace Cairn.AspNetCore.Internal;

/// <summary>Adapts a <see cref="CursorView"/> (from an <c>AddCursorPaging</c> selector) to <see cref="ICursorPagedResource"/>.</summary>
internal sealed class CursorPagedViewAdapter(CursorView view) : ICursorPagedResource
{
    public IEnumerable Items => view.Items;

    public string? Next => view.Next;

    public string? Prev => view.Prev;
}
