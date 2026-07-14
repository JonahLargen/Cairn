namespace Cairn.Mcp.Internal;

/// <summary>A resource type opted into MCP exposure, with the loader that materializes an instance per tool call.</summary>
internal abstract class CairnMcpResourceRegistration
{
    protected CairnMcpResourceRegistration(string name) => Name = name;

    /// <summary>The tool-name prefix (<c>{name}_{affordance}</c>).</summary>
    public string Name { get; }

    /// <summary>Whether tool calls must supply an <c>id</c> for the loader (false for singleton resources).</summary>
    public required bool RequiresId { get; init; }

    /// <summary>The resource CLR type the link configuration is registered for.</summary>
    public abstract Type ResourceType { get; }

    /// <summary>Loads the instance a tool call targets; null means it does not exist (or is unavailable).</summary>
    public abstract ValueTask<object?> LoadAsync(string? id, IServiceProvider services, CancellationToken cancellationToken);
}

/// <summary>The typed registration behind <see cref="CairnMcpOptions.AddResource{T}(string, Func{string, IServiceProvider, CancellationToken, ValueTask{T}})"/>.</summary>
internal sealed class CairnMcpResourceRegistration<T> : CairnMcpResourceRegistration where T : class
{
    private readonly Func<string?, IServiceProvider, CancellationToken, ValueTask<T?>> _loader;

    public CairnMcpResourceRegistration(string name, Func<string?, IServiceProvider, CancellationToken, ValueTask<T?>> loader)
        : base(name) => _loader = loader;

    public override Type ResourceType => typeof(T);

    public override async ValueTask<object?> LoadAsync(string? id, IServiceProvider services, CancellationToken cancellationToken)
        => await _loader(id, services, cancellationToken).ConfigureAwait(false);
}
