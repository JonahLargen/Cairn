namespace Cairn;

/// <summary>Provides the <see cref="LinkConfig{T}"/> registered for a resource type, if any.</summary>
public interface ILinkConfigProvider
{
    /// <summary>Returns the config for <typeparamref name="T"/>, or <see langword="null"/> if none is registered.</summary>
    LinkConfig<T>? GetConfig<T>();
}

/// <summary>An in-memory registry of link configurations keyed by resource type.</summary>
public sealed class LinkConfigRegistry : ILinkConfigProvider
{
    private readonly Dictionary<Type, object> _configs = [];

    /// <summary>Registers a config for <typeparamref name="T"/>, replacing any existing one.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public LinkConfigRegistry Add<T>(LinkConfig<T> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _configs[typeof(T)] = config;
        return this;
    }

    /// <inheritdoc />
    public LinkConfig<T>? GetConfig<T>() => _configs.TryGetValue(typeof(T), out var config) ? (LinkConfig<T>)config : null;
}
