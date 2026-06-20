namespace Cairn.Internal;

/// <summary>
/// A <see cref="LinkConfig{T}"/> compiled once into its specs, able to build a <see cref="LinkSet"/>
/// for an instance supplied as <see cref="object"/> (enabling runtime-type dispatch).
/// </summary>
internal sealed class CompiledLinkConfig<T> : ICompiledLinkConfig
{
    private readonly LinkBuilder<T> _builder;

    private CompiledLinkConfig(LinkBuilder<T> builder) => _builder = builder;

    public static CompiledLinkConfig<T> Compile(LinkConfig<T> config)
    {
        var builder = new LinkBuilder<T>();
        config.Configure(builder);
        return new CompiledLinkConfig<T>(builder);
    }

    public async ValueTask<LinkSet> BuildAsync(object resource, LinkContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var typed = (T)resource;

        var links = new List<Link>(_builder.Links.Count);
        foreach (var spec in _builder.Links)
        {
            if (await IncludeAsync(spec, typed, context, cancellationToken).ConfigureAwait(false)
                && Resolve(spec, typed, context) is { } href)
            {
                links.Add(new Link(spec.Relation, href) { Title = spec.TitleText });
            }
        }

        var affordances = new List<Affordance>(_builder.Affordances.Count);
        foreach (var spec in _builder.Affordances)
        {
            if (await IncludeAsync(spec, typed, context, cancellationToken).ConfigureAwait(false)
                && Resolve(spec, typed, context) is { } href)
            {
                affordances.Add(new Affordance(spec.Relation, href, spec.HttpMethod) { Title = spec.TitleText, Input = spec.InputType });
            }
        }

        return links.Count == 0 && affordances.Count == 0 ? LinkSet.Empty : new LinkSet(links, affordances);
    }

    private static async ValueTask<bool> IncludeAsync(HypermediaSpec<T> spec, T resource, LinkContext context, CancellationToken cancellationToken)
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

    private static string? Resolve(HypermediaSpec<T> spec, T resource, LinkContext context)
    {
        var href = context.UrlResolver.Resolve(spec.Target(resource));

        if (href is null && context.Mode == LinkResolutionMode.Strict)
        {
            throw new LinkResolutionException($"Could not resolve a URL for relation '{spec.Relation}'.");
        }

        return href;
    }
}
