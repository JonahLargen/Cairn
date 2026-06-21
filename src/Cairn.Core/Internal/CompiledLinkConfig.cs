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
            if (!await IncludeAsync(spec, typed, context, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var (target, href) = await ResolveAsync(spec, typed, context).ConfigureAwait(false);
            if (href is not null)
            {
                var templated = target is ExplicitLinkTarget { Templated: true };
                links.Add(new Link(spec.Relation, href, templated) { Title = spec.TitleText, Type = spec.TypeText });
            }
        }

        var affordances = new List<Affordance>(_builder.Affordances.Count);
        foreach (var spec in _builder.Affordances)
        {
            if (!await IncludeAsync(spec, typed, context, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var (_, href) = await ResolveAsync(spec, typed, context).ConfigureAwait(false);
            if (href is not null)
            {
                affordances.Add(new Affordance(spec.Relation, href, spec.HttpMethod) { Title = spec.TitleText, Input = spec.InputType });
            }
        }

        return links.Count == 0 && affordances.Count == 0 ? LinkSet.Empty : new LinkSet(links, affordances);
    }

    private static async ValueTask<bool> IncludeAsync(HypermediaSpec<T> spec, T resource, LinkContext context, CancellationToken cancellationToken)
    {
        if (spec.Condition is not null && !await spec.Condition(resource, context).ConfigureAwait(false))
        {
            return false;
        }

        if (spec.Policy is not null && !await context.Authorizer.AuthorizeAsync(spec.Policy, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return true;
    }

    private static async ValueTask<(LinkTarget Target, string? Href)> ResolveAsync(HypermediaSpec<T> spec, T resource, LinkContext context)
    {
        var target = await spec.Target(resource, context).ConfigureAwait(false);
        var href = context.UrlResolver.Resolve(target);

        // Treat null-or-whitespace as unresolved: Strict throws a clear LinkResolutionException; Lax drops the
        // link (rather than letting the Link/Affordance constructor throw a raw ArgumentException, which would
        // abort serialization and defeat Lax mode).
        if (string.IsNullOrWhiteSpace(href))
        {
            if (context.Mode == LinkResolutionMode.Strict)
            {
                throw new LinkResolutionException($"Could not resolve a URL for relation '{spec.Relation}'.");
            }

            return (target, null);
        }

        return (target, href);
    }
}
