using System.Diagnostics.CodeAnalysis;

namespace Cairn.Client;

/// <summary>A collection fetched from a hypermedia API: each item as a navigable <see cref="Resource{TItem}"/>, plus the collection's own links (e.g. pagination).</summary>
/// <typeparam name="TItem">The item body type.</typeparam>
public sealed class CollectionResource<TItem>
{
    private readonly CairnClient _client;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<Link>> _linksByRelation;

    internal CollectionResource(
        CairnClient client,
        IReadOnlyList<Resource<TItem>> items,
        IReadOnlyDictionary<string, IReadOnlyList<Link>> links,
        IReadOnlyDictionary<string, Affordance> affordances)
    {
        _client = client;
        _linksByRelation = links;
        Items = items;
        Links = LinkMap.Flatten(links);
        Affordances = affordances;
    }

    /// <summary>The items, each with its own value, links, and affordances.</summary>
    public IReadOnlyList<Resource<TItem>> Items { get; }

    /// <summary>The collection's links (e.g. <c>next</c>/<c>prev</c>), keyed by relation. Use <see cref="LinksFor"/> for a relation with several links.</summary>
    public IReadOnlyDictionary<string, Link> Links { get; }

    /// <summary>The collection's affordances, keyed by name.</summary>
    public IReadOnlyDictionary<string, Affordance> Affordances { get; }

    /// <summary>All links sharing the given relation (a HAL link array exposes more than one), or empty if none.</summary>
    public IReadOnlyList<Link> LinksFor(string relation) => _linksByRelation.TryGetValue(relation, out var list) ? list : [];

    /// <summary>Whether the collection exposes a link with the given relation.</summary>
    public bool HasLink(string relation) => Links.ContainsKey(relation);

    /// <summary>Whether the collection exposes the named affordance.</summary>
    public bool HasAffordance(string name) => Affordances.ContainsKey(name);

    /// <summary>
    /// Follows a collection link (e.g. <c>next</c>) to another page of the same item type. A templated link
    /// expands with no variables, so its optional expressions collapse per RFC 6570.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="itemsProperty"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The collection has no link with that relation.</exception>
    public Task<CollectionResult<TItem>> FollowAsync(string relation, string itemsProperty = "items", CancellationToken cancellationToken = default)
    {
        // A bare null second argument binds here (string is more specific than object), not to the
        // (relation, variables) overload — catch it now rather than null-ref reading the items property later.
        if (itemsProperty is null)
        {
            throw new ArgumentNullException(
                nameof(itemsProperty),
                "itemsProperty must not be null. To follow the link with no template variables, call FollowAsync(relation, (object?)null) — a bare null second argument binds to this overload's itemsProperty instead.");
        }

        return Links.TryGetValue(relation, out var link)
            ? _client.FollowCollectionAsync<TItem>(link, variables: null, itemsProperty, cancellationToken)
            : throw new InvalidOperationException($"The collection has no '{relation}' link.");
    }

    /// <summary>Follows a collection link, expanding it as an RFC 6570 URI template with <paramref name="variables"/> (e.g. <c>new { page = 2 }</c>).</summary>
    /// <exception cref="InvalidOperationException">The collection has no link with that relation.</exception>
    /// <exception cref="ArgumentException">The link is not templated but <paramref name="variables"/> were supplied.</exception>
    public Task<CollectionResult<TItem>> FollowAsync(string relation, object? variables, string itemsProperty = "items", CancellationToken cancellationToken = default)
        => Links.TryGetValue(relation, out var link)
            ? _client.FollowCollectionAsync<TItem>(link, variables, itemsProperty, cancellationToken)
            : throw new InvalidOperationException($"The collection has no '{relation}' link.");

    /// <summary>Invokes a collection-level affordance, optionally with a request body and an <c>ifMatch</c> ETag.</summary>
    /// <exception cref="InvalidOperationException">The collection has no affordance with that name.</exception>
    public Task<ClientResult> InvokeAsync(string name, object? body = null, string? ifMatch = null, CancellationToken cancellationToken = default)
        => Affordances.TryGetValue(name, out var affordance)
            ? _client.InvokeAsync(affordance, body, ifMatch, cancellationToken)
            : throw new InvalidOperationException($"The collection has no '{name}' affordance.");
}

/// <summary>The outcome of a request that returns a collection: a <see cref="CollectionResource{TItem}"/> on success, or a <see cref="Client.Problem"/> on an HTTP error status.</summary>
/// <typeparam name="TItem">The item body type.</typeparam>
public sealed class CollectionResult<TItem>
{
    private CollectionResult(bool isSuccess, int status, CollectionResource<TItem>? collection, Problem? problem)
    {
        IsSuccess = isSuccess;
        Status = status;
        Collection = collection;
        Problem = problem;
    }

    /// <summary>
    /// Whether the request succeeded — a 2xx status, or <c>304 Not Modified</c> for a conditional request
    /// (see <see cref="IsNotModified"/>). When <see langword="true"/>, <see cref="Collection"/> is non-null;
    /// otherwise <see cref="Problem"/> is non-null.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Collection))]
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool IsSuccess { get; }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>Whether the server returned <c>304 Not Modified</c> (in response to a conditional request). The result is successful but carries no items.</summary>
    public bool IsNotModified => Status == 304;

    /// <summary>The collection and its hypermedia, when <see cref="IsSuccess"/>.</summary>
    public CollectionResource<TItem>? Collection { get; }

    /// <summary>The parsed problem detail, when not <see cref="IsSuccess"/>.</summary>
    public Problem? Problem { get; }

    /// <summary>Returns the <see cref="Collection"/> on success, otherwise throws.</summary>
    /// <exception cref="CairnClientException">The response was an HTTP error status.</exception>
    public CollectionResource<TItem> EnsureSuccess()
        => IsSuccess ? Collection : throw new CairnClientException(Status, Problem);

    internal static CollectionResult<TItem> Success(int status, CollectionResource<TItem> collection) => new(true, status, collection, null);

    internal static CollectionResult<TItem> Failure(int status, Problem problem) => new(false, status, null, problem);
}
