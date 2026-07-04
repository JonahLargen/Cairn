using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Cairn.Client;

/// <summary>A resource fetched from a Cairn hypermedia API: its typed value plus its links, affordances, and embedded resources.</summary>
/// <typeparam name="T">The resource body type.</typeparam>
public sealed class Resource<T>
{
    private readonly CairnClient _client;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AffordanceField>> _fields;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Link>> _linksByRelation;
    private readonly JsonElement _embedded;

    internal Resource(
        CairnClient client,
        T? value,
        IReadOnlyDictionary<string, IReadOnlyList<Link>> links,
        IReadOnlyDictionary<string, Affordance> affordances,
        IReadOnlyDictionary<string, IReadOnlyList<AffordanceField>> fields,
        string? etag = null,
        JsonElement embedded = default,
        IReadOnlyList<Curie>? curies = null)
    {
        _client = client;
        _fields = fields;
        _linksByRelation = links;
        _embedded = embedded;
        Value = value;
        Links = LinkMap.Flatten(links);
        Affordances = affordances;
        ETag = etag;
        Curies = curies ?? [];
    }

    /// <summary>The deserialized resource body, or <see langword="null"/> if the body could not be deserialized to <typeparamref name="T"/>.</summary>
    public T? Value { get; }

    /// <summary>The deserialized body, or throws if it is null (an empty body, or one that could not be deserialized to <typeparamref name="T"/>).</summary>
    /// <exception cref="InvalidOperationException"><see cref="Value"/> is null.</exception>
    public T RequireValue() => Value ?? throw new InvalidOperationException($"The resource has no deserialized '{typeof(T).Name}' value (the body was empty or could not be deserialized).");

    /// <summary>The response's <c>ETag</c>, if any — pass it as <c>ifMatch</c> to an action for optimistic concurrency.</summary>
    public string? ETag { get; }

    /// <summary>The resource's links, keyed by relation. When a relation has several links (a HAL link array), this exposes the first; use <see cref="LinksFor"/> for all.</summary>
    public IReadOnlyDictionary<string, Link> Links { get; }

    /// <summary>The resource's affordances (available actions), keyed by name.</summary>
    public IReadOnlyDictionary<string, Affordance> Affordances { get; }

    /// <summary>The curie definitions from <c>_links.curies</c>, resolving prefixed relations to documentation.</summary>
    public IReadOnlyList<Curie> Curies { get; }

    /// <summary>
    /// The documentation URL for a curie-prefixed relation (e.g. <c>acme:widget</c>), expanding the matching
    /// curie's <c>{rel}</c> template — or <see langword="null"/> when the relation carries no known prefix.
    /// </summary>
    public string? DocumentationFor(string relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        var colon = relation.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var prefix = relation[..colon];
        foreach (var curie in Curies)
        {
            if (string.Equals(curie.Name, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return curie.Templated
                    ? UriTemplate.Expand(curie.Href, new Dictionary<string, object?> { ["rel"] = relation[(colon + 1)..] })
                    : curie.Href;
            }
        }

        return null;
    }

    /// <summary>All links sharing the given relation (a HAL link array exposes more than one), or empty if none.</summary>
    public IReadOnlyList<Link> LinksFor(string relation) => _linksByRelation.TryGetValue(relation, out var list) ? list : [];

    /// <summary>Whether the resource exposes a link with the given relation.</summary>
    public bool HasLink(string relation) => Links.ContainsKey(relation);

    /// <summary>Whether the resource exposes the named affordance.</summary>
    public bool HasAffordance(string name) => Affordances.ContainsKey(name);

    /// <summary>The input fields the named affordance accepts (from its HAL-FORMS template), or empty if none are described.</summary>
    public IReadOnlyList<AffordanceField> Fields(string name)
        => _fields.TryGetValue(name, out var fields) ? fields : [];

    /// <summary>The resources embedded under the given relation (HAL <c>_embedded</c>), each as a navigable resource; empty if none.</summary>
    /// <typeparam name="TChild">The embedded resource body type.</typeparam>
    public IReadOnlyList<Resource<TChild>> Embedded<TChild>(string relation)
    {
        if (_embedded.ValueKind != JsonValueKind.Object || !TryGetEmbedded(relation, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<Resource<TChild>>();
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    list.Add(_client.BuildResource<TChild>(element));
                }
            }

            return list;
        }

        return value.ValueKind == JsonValueKind.Object ? [_client.BuildResource<TChild>(value)] : [];
    }

    // Relation types are case-insensitive (RFC 8288 §2.1): prefer an exact property match, then fall
    // back to a case-insensitive scan of the _embedded object.
    private bool TryGetEmbedded(string relation, out JsonElement value)
    {
        if (_embedded.TryGetProperty(relation, out value))
        {
            return true;
        }

        foreach (var property in _embedded.EnumerateObject())
        {
            if (string.Equals(property.Name, relation, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Follows the link with the given relation to another resource.</summary>
    /// <exception cref="InvalidOperationException">The resource has no link with that relation.</exception>
    public Task<ClientResult<TNext>> FollowAsync<TNext>(string relation, CancellationToken cancellationToken = default)
        => Links.TryGetValue(relation, out var link)
            ? _client.FollowAsync<TNext>(link, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{relation}' link.");

    /// <summary>Follows the link with the given relation, expanding it as an RFC 6570 URI template with <paramref name="variables"/> (e.g. <c>new { status = "open", page = 2 }</c>).</summary>
    /// <exception cref="InvalidOperationException">The resource has no link with that relation.</exception>
    [RequiresUnreferencedCode(CairnClient.TemplateVariablesRequiresUnreferencedCode)]
    public Task<ClientResult<TNext>> FollowAsync<TNext>(string relation, object? variables, CancellationToken cancellationToken = default)
        => Links.TryGetValue(relation, out var link)
            ? _client.FollowAsync<TNext>(link, variables, cancellationToken)
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

    /// <summary>
    /// Submits the named affordance's HAL-FORMS template: validates <paramref name="values"/> against the
    /// template's fields (required, read-only, regex, length, range, options) before anything is sent, then
    /// sends them with the template's method and declared content type.
    /// </summary>
    /// <exception cref="InvalidOperationException">The resource has no affordance with that name.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> fail client-side validation against the template's fields.</exception>
    public Task<ClientResult> SubmitAsync(string name, object? values = null, string? ifMatch = null, CancellationToken cancellationToken = default)
        => Affordances.TryGetValue(name, out var affordance)
            ? _client.SubmitAsync(affordance, Fields(name), values, ifMatch, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{name}' affordance.");

    /// <summary>
    /// Submits the named affordance's HAL-FORMS template and reads its returned resource: validates
    /// <paramref name="values"/> against the template's fields before anything is sent, then sends them with
    /// the template's method and declared content type.
    /// </summary>
    /// <exception cref="InvalidOperationException">The resource has no affordance with that name.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> fail client-side validation against the template's fields.</exception>
    public Task<ClientResult<TResult>> SubmitAsync<TResult>(string name, object? values = null, string? ifMatch = null, CancellationToken cancellationToken = default)
        => Affordances.TryGetValue(name, out var affordance)
            ? _client.SubmitAsync<TResult>(affordance, Fields(name), values, ifMatch, cancellationToken)
            : throw new InvalidOperationException($"The resource has no '{name}' affordance.");
}
