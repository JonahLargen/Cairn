using System.Reflection;
using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore;

/// <summary>
/// The cursor pagination parameters of the current request, bound from the same query parameter the cursor
/// links swap — <see cref="CairnOptions.CursorQueryParameter"/> (default <c>cursor</c>) — plus the page size
/// from <see cref="CairnOptions.LimitQueryParameter"/> (default <c>limit</c>), so the cursor a handler reads
/// can never drift from the URLs Cairn writes into <c>_links</c>. Opt in per endpoint by declaring a
/// parameter of this type on a minimal-API handler; in a controller action, call
/// <see cref="Bind(HttpContext)"/>.
/// </summary>
/// <remarks>
/// An absent (or empty) cursor binds <see langword="null"/> — the first page — and an absent limit binds
/// <see cref="CairnOptions.DefaultPageSize"/>; a limit above <see cref="CairnOptions.MaxPageSize"/> (when
/// set) is clamped to it. A repeated cursor, or a repeated, non-integer, or non-positive limit fails the
/// request with <c>400 Bad Request</c>. Return the fetched page as a <see cref="CursorPage{T}"/> as usual —
/// the next/prev cursors come from the store, not the request.
/// </remarks>
public sealed class CursorRequest : IBindableFromHttpContext<CursorRequest>, IEndpointParameterMetadataProvider
{
    private CursorRequest(string? cursor, int limit)
    {
        Cursor = cursor;
        Limit = limit;
    }

    /// <summary>The opaque cursor of the requested page, or <see langword="null"/> for the first page.</summary>
    public string? Cursor { get; }

    /// <summary>
    /// The requested page size (<see cref="CairnOptions.DefaultPageSize"/> when the request doesn't supply
    /// one, clamped to <see cref="CairnOptions.MaxPageSize"/> when that is set).
    /// </summary>
    public int Limit { get; }

    /// <summary>
    /// Binds the cursor pagination parameters from <paramref name="context"/>'s query string — for
    /// controller actions and other places outside minimal-API parameter binding. Uses the parameter names
    /// and bounds configured on <see cref="CairnOptions"/>, or their documented defaults when Cairn is not
    /// registered.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">The cursor is repeated, or the limit is repeated, not an integer, or below 1.</exception>
    public static CursorRequest Bind(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.RequestServices.GetService<CairnOptions>();
        var query = context.Request.Query;
        return new(
            PaginationBinding.ReadCursor(query, options?.CursorQueryParameter ?? PaginationBinding.CursorParameter),
            PaginationBinding.ReadPositive(
                query,
                options?.LimitQueryParameter ?? PaginationBinding.LimitParameter,
                fallback: options?.DefaultPageSize ?? PaginationBinding.DefaultPageSize,
                max: options?.MaxPageSize));
    }

    /// <summary>
    /// Binds the cursor pagination parameters for a minimal-API handler parameter of this type. A repeated
    /// cursor, or a repeated, non-integer, or non-positive limit yields <see langword="null"/>, which the
    /// framework rejects with its standard <c>400 Bad Request</c> for a required parameter (the precise
    /// reason is logged); declare the parameter non-nullable so that rejection applies.
    /// </summary>
    public static ValueTask<CursorRequest?> BindAsync(HttpContext context, ParameterInfo parameter)
        => PaginationBinding.BindOrReject(context, Bind);

    // Snapshot the resolved parameter names and bounds onto the endpoint at map time, so the document
    // generators — which cannot see into a BindAsync parameter — describe the query parameters the endpoint
    // actually binds. Resolved here (not in the generators) because they reference only Cairn.Core.
    static void IEndpointParameterMetadataProvider.PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder)
    {
        var options = builder.ApplicationServices.GetService<CairnOptions>();
        builder.Metadata.Add(new CursorBindingMetadata(
            options?.CursorQueryParameter ?? PaginationBinding.CursorParameter,
            options?.LimitQueryParameter ?? PaginationBinding.LimitParameter,
            options?.DefaultPageSize ?? PaginationBinding.DefaultPageSize,
            options?.MaxPageSize));
    }
}
