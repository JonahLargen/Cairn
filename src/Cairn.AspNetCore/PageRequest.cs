using System.Reflection;
using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore;

/// <summary>
/// The offset pagination parameters of the current request, bound from the same query parameters the
/// pagination links swap — <see cref="CairnOptions.PageQueryParameter"/> (default <c>page</c>) and
/// <see cref="CairnOptions.PageSizeQueryParameter"/> (default <c>pageSize</c>) — so the values a handler
/// pages by can never drift from the URLs Cairn writes into <c>_links</c>. Opt in per endpoint by declaring
/// a parameter of this type on a minimal-API handler; in a controller action, call
/// <see cref="Bind(HttpContext)"/>.
/// </summary>
/// <remarks>
/// An absent page number binds 1 and an absent page size binds <see cref="CairnOptions.DefaultPageSize"/>;
/// a page size above <see cref="CairnOptions.MaxPageSize"/> (when set) is clamped to it. A repeated,
/// non-integer, or non-positive value fails the request with <c>400 Bad Request</c> — it is the client's
/// error, and silently coercing it would make the echoed page metadata lie about what was asked for.
/// Wrap the page you fetched with <see cref="ToResource{T}"/> to carry the bound values into the
/// <see cref="PagedResource{T}"/> envelope.
/// </remarks>
public sealed class PageRequest : IBindableFromHttpContext<PageRequest>, IEndpointParameterMetadataProvider
{
    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>The 1-based page number (1 when the request doesn't supply one).</summary>
    public int Page { get; }

    /// <summary>
    /// The page size (<see cref="CairnOptions.DefaultPageSize"/> when the request doesn't supply one,
    /// clamped to <see cref="CairnOptions.MaxPageSize"/> when that is set).
    /// </summary>
    public int PageSize { get; }

    /// <summary>The number of items before this page — <c>(Page - 1) * PageSize</c>, for Skip/Take queries.</summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// Wraps a fetched page in the <see cref="PagedResource{T}"/> envelope, stamped with the bound
    /// <see cref="Page"/> and <see cref="PageSize"/> so the envelope — and the pagination links derived from
    /// it — describe exactly the page the client asked for.
    /// </summary>
    /// <param name="items">The items on this page.</param>
    /// <param name="totalCount">The total number of items across all pages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public PagedResource<T> ToResource<T>(IReadOnlyList<T> items, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(items, Page, PageSize, totalCount);
    }

    /// <summary>
    /// Binds the offset pagination parameters from <paramref name="context"/>'s query string — for
    /// controller actions and other places outside minimal-API parameter binding. Uses the parameter names
    /// and bounds configured on <see cref="CairnOptions"/>, or their documented defaults when Cairn is not
    /// registered.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">A parameter is repeated, not an integer, or below 1.</exception>
    public static PageRequest Bind(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.RequestServices.GetService<CairnOptions>();
        var query = context.Request.Query;
        return new(
            PaginationBinding.ReadPositive(query, options?.PageQueryParameter ?? PaginationBinding.PageParameter, fallback: 1, max: null),
            PaginationBinding.ReadPositive(
                query,
                options?.PageSizeQueryParameter ?? PaginationBinding.PageSizeParameter,
                fallback: options?.DefaultPageSize ?? PaginationBinding.DefaultPageSize,
                max: options?.MaxPageSize));
    }

    /// <summary>
    /// Binds the offset pagination parameters for a minimal-API handler parameter of this type. A repeated,
    /// non-integer, or non-positive value yields <see langword="null"/>, which the framework rejects with its
    /// standard <c>400 Bad Request</c> for a required parameter (the precise reason is logged); declare the
    /// parameter non-nullable so that rejection applies.
    /// </summary>
    public static ValueTask<PageRequest?> BindAsync(HttpContext context, ParameterInfo parameter)
        => PaginationBinding.BindOrReject(context, Bind);

    // Snapshot the resolved parameter names and bounds onto the endpoint at map time, so the document
    // generators — which cannot see into a BindAsync parameter — describe the query parameters the endpoint
    // actually binds. Resolved here (not in the generators) because they reference only Cairn.Core.
    static void IEndpointParameterMetadataProvider.PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder)
    {
        var options = builder.ApplicationServices.GetService<CairnOptions>();
        builder.Metadata.Add(new PageBindingMetadata(
            options?.PageQueryParameter ?? PaginationBinding.PageParameter,
            options?.PageSizeQueryParameter ?? PaginationBinding.PageSizeParameter,
            options?.DefaultPageSize ?? PaginationBinding.DefaultPageSize,
            options?.MaxPageSize));
    }
}
