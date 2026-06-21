namespace Cairn.Client;

/// <summary>A resource fetched from a Cairn hypermedia API: its typed value plus its links and affordances.</summary>
/// <typeparam name="T">The resource body type.</typeparam>
public sealed class Resource<T>
{
    private readonly CairnClient _client;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AffordanceField>> _fields;

    internal Resource(
        CairnClient client,
        T? value,
        IReadOnlyDictionary<string, Link> links,
        IReadOnlyDictionary<string, Affordance> affordances,
        IReadOnlyDictionary<string, IReadOnlyList<AffordanceField>> fields,
        string? etag = null)
    {
        _client = client;
        _fields = fields;
        Value = value;
        Links = links;
        Affordances = affordances;
        ETag = etag;
    }

    /// <summary>The deserialized resource body, or <see langword="null"/> if the body could not be deserialized to <typeparamref name="T"/>.</summary>
    public T? Value { get; }

    /// <summary>The response's <c>ETag</c>, if any — pass it as <c>ifMatch</c> to an action for optimistic concurrency.</summary>
    public string? ETag { get; }

    /// <summary>The resource's links, keyed by relation.</summary>
    public IReadOnlyDictionary<string, Link> Links { get; }

    /// <summary>The resource's affordances (available actions), keyed by name.</summary>
    public IReadOnlyDictionary<string, Affordance> Affordances { get; }

    /// <summary>Whether the resource exposes a link with the given relation.</summary>
    public bool HasLink(string relation) => Links.ContainsKey(relation);

    /// <summary>Whether the resource exposes the named affordance.</summary>
    public bool HasAffordance(string name) => Affordances.ContainsKey(name);

    /// <summary>The input fields the named affordance accepts (from its HAL-FORMS template), or empty if none are described.</summary>
    public IReadOnlyList<AffordanceField> Fields(string name)
        => _fields.TryGetValue(name, out var fields) ? fields : [];

    /// <summary>Follows the link with the given relation to another resource.</summary>
    /// <exception cref="InvalidOperationException">The resource has no link with that relation.</exception>
    public Task<ClientResult<TNext>> FollowAsync<TNext>(string relation, CancellationToken cancellationToken = default)
        => Links.TryGetValue(relation, out var link)
            ? _client.FollowAsync<TNext>(link, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{relation}' link.");

    /// <summary>Invokes the named affordance, optionally with a request body and an <c>ifMatch</c> ETag for optimistic concurrency.</summary>
    /// <exception cref="InvalidOperationException">The resource has no affordance with that name.</exception>
    public Task<ClientResult> InvokeAsync(string name, object? body = null, string? ifMatch = null, CancellationToken cancellationToken = default)
        => Affordances.TryGetValue(name, out var affordance)
            ? _client.InvokeAsync(affordance, body, ifMatch, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{name}' affordance.");

    /// <summary>Invokes the named affordance and reads its returned resource, optionally with an <c>ifMatch</c> ETag.</summary>
    /// <exception cref="InvalidOperationException">The resource has no affordance with that name.</exception>
    public Task<ClientResult<TResult>> InvokeAsync<TResult>(string name, object? body = null, string? ifMatch = null, CancellationToken cancellationToken = default)
        => Affordances.TryGetValue(name, out var affordance)
            ? _client.InvokeAsync<TResult>(affordance, body, ifMatch, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{name}' affordance.");
}
