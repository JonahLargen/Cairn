using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Compute stage: builds the hypermedia for an endpoint's returned value — and each element of a
/// returned collection — recording it by reference for the emit stage to serialize.
/// </summary>
internal static class CairnLinkRecorder
{
    public static async ValueTask RecordAsync(HttpContext http, object? result)
    {
        if (Unwrap(result) is not IValueHttpResult { Value: { } value })
        {
            return;
        }

        var services = http.RequestServices;
        var engine = services.GetRequiredService<ILinkEngine>();
        var options = services.GetRequiredService<CairnOptions>();
        var context = new LinkContext(
            services.GetRequiredService<ILinkUrlResolver>(),
            services.GetRequiredService<ILinkAuthorizer>(),
            options.Mode);

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        await RecordAsync(http, value, engine, context, visited);
    }

    private static async ValueTask RecordAsync(HttpContext http, object? value, ILinkEngine engine, LinkContext context, HashSet<object> visited)
    {
        if (value is null)
        {
            return;
        }

        // Walk collections so each element is linked; the array/list itself carries no _links.
        if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                await RecordAsync(http, item, engine, context, visited);
            }

            return;
        }

        if (!visited.Add(value))
        {
            return;
        }

        var linkSet = await engine.BuildAsync(value, context, http.RequestAborted);
        if (!linkSet.IsEmpty)
        {
            CairnLinkStore.Record(http, value, ToPayload(linkSet));
        }
    }

    // Peel Results<T1,T2,...> unions down to the concrete result that carries the value.
    private static object? Unwrap(object? result)
    {
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        return result;
    }

    private static ResourceHypermedia ToPayload(LinkSet linkSet)
    {
        IReadOnlyDictionary<string, HalLink>? links = linkSet.Links.Count == 0
            ? null
            : linkSet.Links.ToDictionary(
                l => l.Relation.Value,
                l => new HalLink(l.Href) { Title = l.Title, Templated = l.Templated ? true : null });

        IReadOnlyDictionary<string, HalAction>? actions = linkSet.Affordances.Count == 0
            ? null
            : linkSet.Affordances.ToDictionary(
                a => a.Name.Value,
                a => new HalAction(a.Href, a.Method) { Title = a.Title });

        return new ResourceHypermedia(links, actions);
    }
}
