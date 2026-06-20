using System.Collections;
using System.Text.Json.Serialization;

namespace Cairn.AspNetCore;

/// <summary>A page of resources navigated by opaque cursors, for cursor/keyset pagination links.</summary>
public interface ICursorPagedResource
{
    /// <summary>The items on this page.</summary>
    IEnumerable Items { get; }

    /// <summary>The cursor for the next page, or <see langword="null"/> if there is none.</summary>
    string? Next { get; }

    /// <summary>The cursor for the previous page, or <see langword="null"/> if there is none.</summary>
    string? Previous { get; }
}

/// <summary>A page of <typeparamref name="T"/> navigated by opaque cursors. Return this from an endpoint to get cursor pagination links.</summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Next">The cursor for the next page, or <see langword="null"/> if there is none.</param>
/// <param name="Previous">The cursor for the previous page, or <see langword="null"/> if there is none.</param>
public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    [property: JsonIgnore] string? Next = null,
    [property: JsonIgnore] string? Previous = null) : ICursorPagedResource
{
    IEnumerable ICursorPagedResource.Items => Items;
}

/// <summary>Cursor page metadata for an arbitrary envelope, supplied via <c>AddCursorPaging</c> without implementing <see cref="ICursorPagedResource"/>.</summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Next">The cursor for the next page, or <see langword="null"/> if there is none.</param>
/// <param name="Previous">The cursor for the previous page, or <see langword="null"/> if there is none.</param>
public readonly record struct CursorView(IEnumerable Items, string? Next, string? Previous);
