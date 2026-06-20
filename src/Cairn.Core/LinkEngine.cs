namespace Cairn;

/// <summary>Builds the <see cref="LinkSet"/> for a resource, dispatching by its runtime type.</summary>
public interface ILinkEngine
{
    /// <summary>Builds the links and affordances for <paramref name="resource"/> using its runtime type's configuration.</summary>
    ValueTask<LinkSet> BuildAsync(object resource, LinkContext context, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="ILinkEngine"/>: resolves the resource's compiled config by runtime type.</summary>
public sealed class LinkEngine : ILinkEngine
{
    private readonly ILinkConfigProvider _configs;

    /// <summary>Creates the engine over the given configuration source.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="configs"/> is null.</exception>
    public LinkEngine(ILinkConfigProvider configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        _configs = configs;
    }

    /// <inheritdoc />
    public ValueTask<LinkSet> BuildAsync(object resource, LinkContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (resource is null)
        {
            return new ValueTask<LinkSet>(LinkSet.Empty);
        }

        var config = _configs.GetConfig(resource.GetType());
        return config is null
            ? new ValueTask<LinkSet>(LinkSet.Empty)
            : config.BuildAsync(resource, context, cancellationToken);
    }
}
