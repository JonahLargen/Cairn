using Cairn.Internal;

namespace Cairn;

/// <summary>A link configuration compiled for a resource type, able to build a <see cref="LinkSet"/> for an instance.</summary>
public interface ICompiledLinkConfig
{
    /// <summary>Builds the links and affordances for <paramref name="resource"/>.</summary>
    ValueTask<LinkSet> BuildAsync(object resource, LinkContext context, CancellationToken cancellationToken = default);
}

/// <summary>Provides the compiled link configuration registered for a resource type, if any.</summary>
public interface ILinkConfigProvider
{
    /// <summary>Returns the config for <paramref name="resourceType"/>, or <see langword="null"/> if none is registered.</summary>
    ICompiledLinkConfig? GetConfig(Type resourceType);
}

/// <summary>An in-memory registry of link configurations keyed by resource type.</summary>
public sealed class LinkConfigRegistry : ILinkConfigProvider
{
    private readonly Dictionary<Type, ICompiledLinkConfig> _configs = [];

    /// <summary>Registers the config for <typeparamref name="T"/>, replacing any existing one.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public LinkConfigRegistry Add<T>(LinkConfig<T> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _configs[typeof(T)] = CompiledLinkConfig<T>.Compile(config);
        return this;
    }

    /// <inheritdoc />
    public ICompiledLinkConfig? GetConfig(Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        return _configs.TryGetValue(resourceType, out var config) ? config : null;
    }
}
