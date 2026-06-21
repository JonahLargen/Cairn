namespace Cairn.Client;

/// <summary>A resource fetched from a Cairn hypermedia API: its typed value plus its links and affordances.</summary>
/// <typeparam name="T">The resource body type.</typeparam>
public sealed class Resource<T>
{
    private readonly CairnClient _client;

    internal Resource(CairnClient client, T? value, IReadOnlyDictionary<string, Link> links, IReadOnlyDictionary<string, Affordance> affordances)
    {
        _client = client;
        Value = value;
        Links = links;
        Affordances = affordances;
    }

    /// <summary>The deserialized resource body.</summary>
    public T? Value { get; }

    /// <summary>The resource's links, keyed by relation.</summary>
    public IReadOnlyDictionary<string, Link> Links { get; }

    /// <summary>The resource's affordances (available actions), keyed by name.</summary>
    public IReadOnlyDictionary<string, Affordance> Affordances { get; }

    /// <summary>Whether the resource exposes a link with the given relation.</summary>
    public bool HasLink(string relation) => Links.ContainsKey(relation);

    /// <summary>Whether the resource exposes the named affordance.</summary>
    public bool HasAffordance(string name) => Affordances.ContainsKey(name);

    /// <summary>Follows the link with the given relation to another resource.</summary>
    /// <exception cref="InvalidOperationException">The resource has no link with that relation.</exception>
    public Task<Resource<TNext>> FollowAsync<TNext>(string relation, CancellationToken cancellationToken = default)
        => Links.TryGetValue(relation, out var link)
            ? _client.FollowAsync<TNext>(link, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{relation}' link.");

    /// <summary>Invokes the named affordance, optionally with a request body, returning the raw response.</summary>
    /// <exception cref="InvalidOperationException">The resource has no affordance with that name.</exception>
    public Task<HttpResponseMessage> InvokeAsync(string name, object? body = null, CancellationToken cancellationToken = default)
        => Affordances.TryGetValue(name, out var affordance)
            ? _client.InvokeAsync(affordance, body, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{name}' affordance.");
}
