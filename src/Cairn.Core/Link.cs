namespace Cairn;

/// <summary>An immutable hypermedia link to a related resource or available transition.</summary>
public sealed record Link
{
    /// <summary>Creates a link.</summary>
    /// <exception cref="ArgumentException"><paramref name="href"/> is null or whitespace.</exception>
    public Link(LinkRelation relation, string href, bool templated = false)
    {
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
}
