using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

/// <summary>
/// Describes an embedded-resource relation a link configuration declares, so document generators
/// (Cairn.OpenApi, Cairn.Swashbuckle) can type the <c>_embedded</c> schema with the child resource type
/// rather than an untyped object.
/// </summary>
/// <param name="Relation">The relation the child resource is embedded under.</param>
/// <param name="ResourceType">The CLR type of the embedded child resource (the <c>TChild</c> of <c>Embed</c>/<c>EmbedMany</c>).</param>
/// <param name="Single">Whether the relation embeds a single resource (an object) rather than a collection (an array).</param>
public sealed record EmbeddedResourceSchema(LinkRelation Relation, Type ResourceType, bool Single);

/// <summary>
/// A compiled config that can report the embedded-resource relations it declares. Document generators query
/// this (over <see cref="ICompiledLinkConfig"/>) to type the <c>_embedded</c> schema; the runtime wire never
/// needs it. Kept separate from <see cref="ICompiledLinkConfig"/> so consumers that only build links are
/// unaffected.
/// </summary>
public interface IEmbeddedResourceReportingConfig
{
    /// <summary>The embedded-resource relations declared by the configuration, in declaration order.</summary>
    IReadOnlyList<EmbeddedResourceSchema> EmbeddedResources { get; }
}

/// <summary>
/// Describes an affordance a link configuration declares — the declaration-time facts (name, method, input
/// type, gates) that exist before any resource instance is available. Tool and document generators
/// (e.g. Cairn.Mcp) read this to describe actions statically; the target URL is not included because it is
/// computed per instance.
/// </summary>
/// <param name="Name">The name identifying the action (the relation).</param>
/// <param name="Method">The HTTP method used to invoke the action.</param>
public sealed record AffordanceSchema(LinkRelation Name, string Method)
{
    /// <summary>The name identifying the action (the relation).</summary>
    /// <exception cref="ArgumentException">The value is <c>default(LinkRelation)</c>.</exception>
    public LinkRelation Name { get; init; } = ThrowIfDefaultName(Name);

    /// <summary>The HTTP method used to invoke the action.</summary>
    /// <exception cref="ArgumentException">The value is null or whitespace.</exception>
    public string Method { get; init; } = !string.IsNullOrWhiteSpace(Method)
        ? Method
        : throw new ArgumentException("Affordance method must not be null or whitespace.", nameof(Method));

    /// <summary>The declared input type the action accepts (<c>Accepts&lt;TInput&gt;()</c>), if any.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type? Input { get; init; }

    /// <summary>The declared human-readable title, if any.</summary>
    public string? Title { get; init; }

    /// <summary>The declared content type the action's input is submitted as, if any.</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether the affordance is marked as the resource's primary action (<c>AsDefault()</c>).</summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// The authorization policy gating the affordance, or <see langword="null"/> when it has no policy gate.
    /// The empty string is the sentinel for the host's default policy (<c>RequireAuthorization()</c>).
    /// </summary>
    public string? Policy { get; init; }

    /// <summary>
    /// Whether <see cref="Policy"/> is evaluated against a resource object (resource-based authorization) and
    /// therefore cannot be decided from the caller alone.
    /// </summary>
    public bool PolicyIsResourceBased { get; init; }

    /// <summary>Whether the affordance is gated by a <c>When</c> predicate (its availability depends on resource state).</summary>
    public bool HasCondition { get; init; }

    private static LinkRelation ThrowIfDefaultName(LinkRelation name)
    {
        name.ThrowIfDefault(nameof(Name));
        return name;
    }
}

/// <summary>
/// A compiled config that can report the affordances it declares. Tool and document generators query this
/// (over <see cref="ICompiledLinkConfig"/>) to describe actions without a resource instance; the runtime wire
/// never needs it. Kept separate from <see cref="ICompiledLinkConfig"/> so consumers that only build links
/// are unaffected.
/// </summary>
public interface IAffordanceReportingConfig
{
    /// <summary>The affordances declared by the configuration, in declaration order.</summary>
    IReadOnlyList<AffordanceSchema> Affordances { get; }
}

/// <summary>
/// An in-memory registry of link configurations keyed by resource type. Lookup honors inheritance: a
/// resource type with no config of its own uses the config of its nearest registered base class (so a
/// <c>LinkConfig&lt;OrderDto&gt;</c> also covers <c>RushOrderDto : OrderDto</c>). Interfaces are not
/// considered.
/// </summary>
public sealed class LinkConfigRegistry : ILinkConfigProvider
{
    private readonly object _writeLock = new();

    // Both maps are replaced wholesale (copy-on-write) rather than mutated, so lock-free readers always see
    // a consistent snapshot. The cache holds per-runtime-type resolutions (including negative results); Add
    // publishes a fresh cache *after* the new config map, so a GetConfig racing an Add can only ever store a
    // stale negative into the generation being discarded — never into the current one.
    private Dictionary<Type, ICompiledLinkConfig> _configs = [];
    private ConcurrentDictionary<Type, ICompiledLinkConfig?> _resolved = new();

    /// <summary>Registers the config for <typeparamref name="T"/>, replacing any existing one.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public LinkConfigRegistry Add<T>(LinkConfig<T> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Publish(typeof(T), CompiledLinkConfig<T>.Compile(config));
        return this;
    }

    /// <summary>Registers a <see cref="LinkConfig{T}"/> instance whose resource type is known only at runtime (e.g. from assembly scanning).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="config"/> does not derive from <see cref="LinkConfig{T}"/>.</exception>
    [RequiresDynamicCode("Compiles the config through MakeGenericType over its runtime resource type. Use Add<T>(LinkConfig<T>) in Native AOT applications.")]
    public LinkConfigRegistry Add(object config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var resourceType = ResourceTypeOf(config.GetType())
            ?? throw new ArgumentException($"'{config.GetType().Name}' does not derive from LinkConfig<T>.", nameof(config));

        var compiled = (ICompiledLinkConfig)typeof(CompiledLinkConfig<>)
            .MakeGenericType(resourceType)
            .GetMethod(nameof(CompiledLinkConfig<object>.Compile), BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [config])!;
        Publish(resourceType, compiled);
        return this;
    }

    private void Publish(Type resourceType, ICompiledLinkConfig compiled)
    {
        lock (_writeLock)
        {
            var configs = new Dictionary<Type, ICompiledLinkConfig>(_configs) { [resourceType] = compiled };
            _configs = configs;

            // Release-publish an empty cache after the config map: a reader that acquires this cache is
            // guaranteed to resolve against (at least) the config map above.
            Volatile.Write(ref _resolved, new ConcurrentDictionary<Type, ICompiledLinkConfig?>());
        }
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

    // A point-in-time view of the registered configs, for startup validation.
    internal IReadOnlyDictionary<Type, ICompiledLinkConfig> Snapshot => _configs;

    /// <summary>
    /// Whether a config is registered for a proper subtype of <paramref name="baseType"/> — i.e. the type is a
    /// polymorphic base whose configured subtypes may be serialized through its declared-type contract, even
    /// though the base itself has no config. Reads the same copy-on-write config map as
    /// <see cref="GetConfig"/>.
    /// </summary>
    internal bool HasConfiguredSubtype(Type baseType)
    {
        var configs = _configs;
        foreach (var configured in configs.Keys)
        {
            if (configured != baseType && baseType.IsAssignableFrom(configured))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the config registered for <paramref name="resourceType"/>, falling back to its nearest
    /// registered base class, or <see langword="null"/> if neither exists.
    /// </summary>
    public ICompiledLinkConfig? GetConfig(Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);

        // Acquire the current cache generation once; a concurrent Add swaps in a fresh generation, so any
        // stale (possibly negative) entry this call computes lands only in the abandoned snapshot.
        var resolved = Volatile.Read(ref _resolved);

        // Hit first: converting the Resolve method group allocates a fresh delegate per call (instance
        // method groups are never cached), and this runs once per linked resource on the hot path.
        return resolved.TryGetValue(resourceType, out var config)
            ? config
            : resolved.GetOrAdd(resourceType, Resolve);
    }

    private ICompiledLinkConfig? Resolve(Type resourceType)
    {
        var configs = _configs;
        for (var type = resourceType; type is not null; type = type.BaseType)
        {
            if (configs.TryGetValue(type, out var config))
            {
                return config;
            }
        }

        return null;
    }
}
