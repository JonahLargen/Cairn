using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
        IReadOnlyDictionary<string, Affordance> affordances,
        string? etag = null)
    {
        _client = client;
        _linksByRelation = links;
        Items = items;
        Links = LinkMap.Flatten(links);
        Affordances = affordances;
        ETag = etag;
    }

    /// <summary>The items, each with its own value, links, and affordances.</summary>
    public IReadOnlyList<Resource<TItem>> Items { get; }

    /// <summary>The response's <c>ETag</c>, if any — pass it as <c>ifNoneMatch</c> to a later <see cref="CairnClient.GetCollectionAsync{TItem}(string, string, string, System.Threading.CancellationToken)"/> for a conditional read.</summary>
    public string? ETag { get; }

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
            ? _client.FollowCollectionAsync<TItem>(link, itemsProperty, cancellationToken)
            : throw new InvalidOperationException($"The collection has no '{relation}' link.");
    }

    /// <summary>Follows a collection link, expanding it as an RFC 6570 URI template with <paramref name="variables"/> (e.g. <c>new { page = 2 }</c>).</summary>
    /// <exception cref="InvalidOperationException">The collection has no link with that relation.</exception>
    /// <exception cref="ArgumentException">The link is not templated but <paramref name="variables"/> were supplied.</exception>
    [RequiresUnreferencedCode(CairnClient.TemplateVariablesRequiresUnreferencedCode)]
    public Task<CollectionResult<TItem>> FollowAsync(string relation, object? variables, string itemsProperty = "items", CancellationToken cancellationToken = default)
        => Links.TryGetValue(relation, out var link)
            ? _client.FollowCollectionAsync<TItem>(link, variables, itemsProperty, cancellationToken)
            : throw new InvalidOperationException($"The collection has no '{relation}' link.");

    /// <summary>
    /// Traverson-style multi-hop navigation: follows each relation in <paramref name="relations"/> in turn —
    /// the first from this collection's links, each subsequent one from the resource the previous link led
    /// to — binding only the final response to <typeparamref name="TNext"/>. Does not throw on an HTTP error
    /// status: a failing hop ends the traversal with that hop's failure result.
    /// </summary>
    /// <remarks>
    /// Intermediate hops are read for hypermedia only. A templated link along the chain expands with no
    /// variables, so its optional expressions collapse per RFC 6570 (matching
    /// <see cref="FollowAsync(string, string, CancellationToken)"/>); a hop that needs variables must be
    /// followed hop-by-hop. The configured link policy is enforced on every hop.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="relations"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="relations"/> is empty, or contains a null or empty relation.</exception>
    /// <exception cref="InvalidOperationException">This collection, or a resource along the chain, has no link with the next relation.</exception>
    public Task<ClientResult<TNext>> TraverseAsync<TNext>(params string[] relations)
        => TraverseAsync<TNext>(relations, CancellationToken.None);

    /// <summary>Traverson-style multi-hop navigation with a <see cref="CancellationToken"/>: see <see cref="TraverseAsync{TNext}(string[])"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="relations"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="relations"/> is empty, or contains a null or empty relation.</exception>
    /// <exception cref="InvalidOperationException">This collection, or a resource along the chain, has no link with the next relation.</exception>
    public Task<ClientResult<TNext>> TraverseAsync<TNext>(string[] relations, CancellationToken cancellationToken)
    {
        CairnClient.ValidateRelations(relations);
        return Links.TryGetValue(relations[0], out var link)
            ? _client.TraverseFromAsync<TNext>(link, relations, cancellationToken)
            : throw new InvalidOperationException($"The collection has no '{relations[0]}' link.");
    }

    /// <summary>
    /// Streams every item across pages as an asynchronous sequence: yields this page's items, then follows the
    /// <paramref name="relation"/> link (default <c>next</c>) to the following page and yields its items, and so
    /// on until a page carries no such link — walking the collection to exhaustion. Each page is fetched lazily,
    /// only as the enumeration reaches it.
    /// </summary>
    /// <param name="relation">The pagination relation to walk (default <c>next</c>).</param>
    /// <param name="itemsProperty">The array property naming the items on each page's envelope (default <c>items</c>); a bare JSON array is read directly.</param>
    /// <param name="maxItems">An optional cap on the total number of items yielded; enumeration stops once this many have been produced, even mid-page. <see langword="null"/> (the default) walks without an item cap.</param>
    /// <param name="maxPages">An optional cap on the total number of pages read, counting the page this is called on; <c>1</c> yields only this page and never follows the link. <see langword="null"/> (the default) walks without a page cap.</param>
    /// <param name="cancellationToken">A token observed while fetching each following page.</param>
    /// <remarks>
    /// A templated pagination link is followed with no variables, so its optional expressions collapse per
    /// RFC 6570 (matching <see cref="FollowAsync(string, string, CancellationToken)"/>). With neither cap set the
    /// walk trusts the server to end the chain; set <paramref name="maxPages"/> or <paramref name="maxItems"/> to
    /// bound a walk against an untrusted or misbehaving server (for example one whose <c>next</c> cycles). A page
    /// fetch that returns an HTTP error status throws <see cref="CairnClientException"/> from the enumeration —
    /// the failing page has no items to yield and no link to continue from.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="relation"/> or <paramref name="itemsProperty"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxItems"/> or <paramref name="maxPages"/> is less than 1.</exception>
    public IAsyncEnumerable<Resource<TItem>> EnumerateItemsAsync(
        string relation = "next",
        string itemsProperty = "items",
        int? maxItems = null,
        int? maxPages = null,
        CancellationToken cancellationToken = default)
    {
        // Validate eagerly: an iterator method defers its whole body to the first MoveNextAsync, so a caller that
        // passes a bad argument must be told when it calls, not when it starts enumerating.
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(itemsProperty);
        if (maxItems is { } itemCap)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemCap, nameof(maxItems));
        }

        if (maxPages is { } pageCap)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCap, nameof(maxPages));
        }

        return IterateAsync(relation, itemsProperty, maxItems, maxPages, cancellationToken);
    }

    private async IAsyncEnumerable<Resource<TItem>> IterateAsync(
        string relation,
        string itemsProperty,
        int? maxItems,
        int? maxPages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = this;
        var yielded = 0;
        var pagesRead = 1;   // the page this was called on is already in hand.

        while (true)
        {
            foreach (var item in page.Items)
            {
                if (maxItems is { } cap && yielded >= cap)
                {
                    yield break;
                }

                yield return item;
                yielded++;
            }

            // Stop before fetching another page when the item cap is exactly met, the page cap is reached, or the
            // current page exposes no link to follow — so a walk that ends on a page boundary spends no extra request.
            if ((maxItems is { } itemCap && yielded >= itemCap)
                || (maxPages is { } pageCap && pagesRead >= pageCap)
                || !page.HasLink(relation))
            {
                yield break;
            }

            page = (await page.FollowAsync(relation, itemsProperty, cancellationToken).ConfigureAwait(false)).EnsureSuccess();
            pagesRead++;
        }
    }

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
