using System.Collections;

namespace Cairn.AspNetCore;

/// <summary>A page of resources exposing paging metadata, for automatic pagination links.</summary>
public interface IPagedResource
{
    /// <summary>The items on this page.</summary>
    IEnumerable Items { get; }

    /// <summary>The 1-based page number.</summary>
    int Page { get; }

    /// <summary>The page size.</summary>
    int PageSize { get; }

    /// <summary>The total number of items across all pages.</summary>
    int TotalCount { get; }

    /// <summary>The total number of pages.</summary>
    int TotalPages { get; }
}

/// <summary>A page of <typeparamref name="T"/> with paging metadata. Return this from an endpoint to get pagination links.</summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Page">The 1-based page number.</param>
/// <param name="PageSize">The page size.</param>
/// <param name="TotalCount">The total number of items across all pages.</param>
public sealed record PagedResource<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount) : IPagedResource
{
    /// <summary>The total number of pages.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;

    IEnumerable IPagedResource.Items => Items;
}

/// <summary>Page metadata for an arbitrary envelope, supplied via <c>AddPaging</c> without implementing <see cref="IPagedResource"/>.</summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Page">The 1-based page number.</param>
/// <param name="PageSize">The page size.</param>
/// <param name="TotalCount">The total number of items across all pages.</param>
public readonly record struct PagedView(IEnumerable Items, int Page, int PageSize, int TotalCount);
