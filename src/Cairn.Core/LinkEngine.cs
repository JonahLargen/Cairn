using System.Collections.Concurrent;
using Cairn.Internal;

namespace Cairn;

/// <summary>Builds the <see cref="LinkSet"/> for a resource from its registered configuration.</summary>
public interface ILinkEngine
{
    /// <summary>Builds the links and affordances for <paramref name="resource"/>.</summary>
    ValueTask<LinkSet> BuildAsync<T>(T resource, LinkContext context, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="ILinkEngine"/>: evaluates conditions and authorization, then resolves URLs.</summary>
public sealed class LinkEngine : ILinkEngine
{
    private readonly ILinkConfigProvider _configs;
    private readonly ConcurrentDictionary<Type, object> _builders = new();

    /// <summary>Creates the engine over the given configuration source.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="configs"/> is null.</exception>
    public LinkEngine(ILinkConfigProvider configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        _configs = configs;
    }

    /// <inheritdoc />
    public async ValueTask<LinkSet> BuildAsync<T>(T resource, LinkContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (resource is null)
        {
            return LinkSet.Empty;
        }

        var builder = GetBuilder<T>();
        if (builder is null)
        {
            return LinkSet.Empty;
        }

        var links = new List<Link>(builder.Links.Count);
        foreach (var spec in builder.Links)
        {
            if (!await IncludeAsync(spec, resource, context, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (Resolve(spec, resource, context) is { } href)
            {
                links.Add(new Link(spec.Relation, href) { Title = spec.TitleText });
            }
        }

        var affordances = new List<Affordance>(builder.Affordances.Count);
        foreach (var spec in builder.Affordances)
        {
            if (!await IncludeAsync(spec, resource, context, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (Resolve(spec, resource, context) is { } href)
            {
                affordances.Add(new Affordance(spec.Relation, href, spec.HttpMethod) { Title = spec.TitleText });
            }
        }

        return links.Count == 0 && affordances.Count == 0 ? LinkSet.Empty : new LinkSet(links, affordances);
    }

    private LinkBuilder<T>? GetBuilder<T>()
    {
        if (_builders.TryGetValue(typeof(T), out var cached))
        {
            return (LinkBuilder<T>?)cached;
        }

        var config = _configs.GetConfig<T>();
        if (config is null)
        {
            return null;
        }

        var builder = new LinkBuilder<T>();
        config.Configure(builder);
        _builders.TryAdd(typeof(T), builder);
        return builder;
    }

    private static async ValueTask<bool> IncludeAsync<T>(HypermediaSpec<T> spec, T resource, LinkContext context, CancellationToken cancellationToken)
    {
        if (spec.Condition is not null && !spec.Condition(resource))
        {
            return false;
        }

        if (spec.Policy is not null && !await context.Authorizer.AuthorizeAsync(spec.Policy, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return true;
    }

    private static string? Resolve<T>(HypermediaSpec<T> spec, T resource, LinkContext context)
    {
        var target = spec.Target(resource);
        var href = context.UrlResolver.Resolve(target);

        if (href is null && context.Mode == LinkResolutionMode.Strict)
        {
            throw new LinkResolutionException($"Could not resolve a URL for relation '{spec.Relation}'.");
        }

        return href;
    }
}
