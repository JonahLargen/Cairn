using System.Reflection;
using Cairn.Internal;

namespace Cairn;

/// <summary>A link configuration compiled for a resource type, able to build a <see cref="LinkSet"/> for an instance.</summary>
public interface ICompiledLinkConfig
{
    /// <summary>Builds the links and affordances for <paramref name="resource"/>.</summary>
    /// <exception cref="LinkResolutionException">A link target cannot be resolved and <paramref name="context"/> is in strict mode (<see cref="LinkResolutionMode.Strict"/>).</exception>
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

    /// <summary>Registers a <see cref="LinkConfig{T}"/> instance whose resource type is known only at runtime (e.g. from assembly scanning).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="config"/> does not derive from <see cref="LinkConfig{T}"/>.</exception>
    public LinkConfigRegistry Add(object config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var resourceType = ResourceTypeOf(config.GetType())
            ?? throw new ArgumentException($"'{config.GetType().Name}' does not derive from LinkConfig<T>.", nameof(config));

        _configs[resourceType] = (ICompiledLinkConfig)typeof(CompiledLinkConfig<>)
            .MakeGenericType(resourceType)
            .GetMethod(nameof(CompiledLinkConfig<object>.Compile), BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [config])!;
        return this;
    }

    private static Type? ResourceTypeOf(Type configType)
    {
        for (var type = configType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LinkConfig<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <inheritdoc />
    public ICompiledLinkConfig? GetConfig(Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        return _configs.TryGetValue(resourceType, out var config) ? config : null;
    }
}
