using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Reads the pagination query parameters <see cref="PageRequest"/> and <see cref="CursorRequest"/> bind,
/// and declares the parameter names and page-size default they fall back to when Cairn itself is not
/// registered (so the binding types work — with the documented defaults — in a host that never called
/// <c>AddCairn</c>).
/// </summary>
internal static class PaginationBinding
{
    /// <summary>The default offset page-number parameter (<see cref="CairnOptions.PageQueryParameter"/>).</summary>
    public const string PageParameter = "page";

    /// <summary>The default offset page-size parameter (<see cref="CairnOptions.PageSizeQueryParameter"/>).</summary>
    public const string PageSizeParameter = "pageSize";

    /// <summary>The default cursor parameter (<see cref="CairnOptions.CursorQueryParameter"/>).</summary>
    public const string CursorParameter = "cursor";

    /// <summary>The default cursor page-size parameter (<see cref="CairnOptions.LimitQueryParameter"/>).</summary>
    public const string LimitParameter = "limit";

    /// <summary>The default page size (<see cref="CairnOptions.DefaultPageSize"/>).</summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// Reads a positive integer query parameter: <paramref name="fallback"/> when absent, capped at
    /// <paramref name="max"/> when supplied above it. A repeated, non-integer, or non-positive value is the
    /// client's error, so it fails the request with 400 rather than being silently coerced.
    /// </summary>
    /// <exception cref="BadHttpRequestException">The parameter is repeated, not an integer, or below 1.</exception>
    public static int ReadPositive(IQueryCollection query, string name, int fallback, int? max)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return fallback;
        }

        if (values.Count > 1)
        {
            throw RepeatedParameter(name);
        }

        var raw = values[0];
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new BadHttpRequestException(
                $"The query parameter '{name}' must be an integer greater than or equal to 1, but was '{raw}'.");
        }

        return max is { } cap && value > cap ? cap : value;
    }

    /// <summary>
    /// Reads an opaque cursor query parameter: <see langword="null"/> when absent or empty (both mean
    /// "the first page").
    /// </summary>
    /// <exception cref="BadHttpRequestException">The parameter is repeated.</exception>
    public static string? ReadCursor(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }

        if (values.Count > 1)
        {
            throw RepeatedParameter(name);
        }

        return string.IsNullOrEmpty(values[0]) ? null : values[0];
    }

    private static BadHttpRequestException RepeatedParameter(string name)
        => new($"The query parameter '{name}' must be supplied at most once.");

    /// <summary>
    /// Runs <paramref name="bind"/> for a minimal-API <c>BindAsync</c>, converting a client error into
    /// <see langword="null"/>. Minimal APIs don't catch exceptions a custom <c>BindAsync</c> throws — they
    /// would surface as a 500 — but a <see langword="null"/> for a required parameter gets the framework's
    /// standard 400, the same outcome as its own <c>TryParse</c> binding failures. That path can't carry the
    /// reason, so it is logged here.
    /// </summary>
    public static ValueTask<T?> BindOrReject<T>(HttpContext context, Func<HttpContext, T> bind)
        where T : class
    {
        try
        {
            return ValueTask.FromResult<T?>(bind(context));
        }
        catch (BadHttpRequestException failure)
        {
            context.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(T).FullName!)
                .LogDebug("Pagination binding rejected the request: {Reason}", failure.Message);
            return ValueTask.FromResult<T?>(null);
        }
    }
}
