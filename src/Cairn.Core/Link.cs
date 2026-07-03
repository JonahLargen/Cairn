namespace Cairn;

/// <summary>An immutable hypermedia link to a related resource or available transition.</summary>
public sealed record Link
{
    /// <summary>Creates a link.</summary>
    /// <exception cref="ArgumentException"><paramref name="relation"/> is <c>default(LinkRelation)</c>, or <paramref name="href"/> is null or whitespace.</exception>
    public Link(LinkRelation relation, string href, bool templated = false)
    {
        relation.ThrowIfDefault(nameof(relation));
        if (string.IsNullOrWhiteSpace(href))
        {
            throw new ArgumentException("Link href must not be null or whitespace.", nameof(href));
        }

        Relation = relation;
        Href = href;
        Templated = templated;
    }

    /// <summary>The link relation (the "rel").</summary>
    public LinkRelation Relation { get; init; }

    /// <summary>The target URI, or a URI template when <see cref="Templated"/> is <see langword="true"/>.</summary>
    public string Href { get; init; }

    /// <summary>Whether <see cref="Href"/> is an RFC 6570 URI template.</summary>
    public bool Templated { get; init; }

    /// <summary>An optional human-readable title for the link's destination.</summary>
    public string? Title { get; init; }

    /// <summary>An optional media type hint for the link's destination.</summary>
    public string? Type { get; init; }

    /// <summary>An optional secondary key for selecting between links that share a relation (HAL/RFC 8288 <c>name</c>).</summary>
    public string? Name { get; init; }

    /// <summary>An optional URL whose presence marks the link deprecated; it should describe the deprecation.</summary>
    public string? Deprecation { get; init; }

    /// <summary>An optional language hint for the link's destination (RFC 8288 <c>hreflang</c>).</summary>
    public string? Hreflang { get; init; }

    /// <summary>An optional profile URI describing the link's destination (RFC 6906 <c>profile</c>).</summary>
    public string? Profile { get; init; }
}
