using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Compute stage: builds the hypermedia for an endpoint's returned value and records it by reference
/// for the emit stage to serialize.
/// </summary>
internal static class CairnLinkRecorder
{
    public static async ValueTask RecordAsync<T>(HttpContext http, object? result)
    {
        if (Unwrap(result) is not IValueHttpResult { Value: { } value } || value is not T typed)
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

        var linkSet = await engine.BuildAsync(typed, context, http.RequestAborted);
        if (linkSet.IsEmpty)
        {
            return;
        }

        CairnLinkStore.Record(http, value, ToPayload(linkSet));
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
