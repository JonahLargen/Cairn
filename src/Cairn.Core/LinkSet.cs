namespace Cairn;

/// <summary>The links, affordances, and embedded resources computed for a single resource.</summary>
public sealed class LinkSet
{
    /// <summary>An empty set with no links, affordances, or embedded resources.</summary>
    public static readonly LinkSet Empty = new([], [], []);

    /// <summary>Creates a link set.</summary>
    public LinkSet(IReadOnlyList<Link> links, IReadOnlyList<Affordance> affordances, IReadOnlyList<EmbeddedResource>? embedded = null)
    {
        Links = links;
        Affordances = affordances;
        Embedded = embedded ?? [];
    }

    /// <summary>The resource's links.</summary>
    public IReadOnlyList<Link> Links { get; }

    /// <summary>The resource's affordances (available actions).</summary>
    public IReadOnlyList<Affordance> Affordances { get; }

    /// <summary>The resources embedded under this resource (HAL <c>_embedded</c>).</summary>
    public IReadOnlyList<EmbeddedResource> Embedded { get; }

    /// <summary>Whether the set contains no links, affordances, or embedded resources.</summary>
    public bool IsEmpty => Links.Count == 0 && Affordances.Count == 0 && Embedded.Count == 0;
}

/// <summary>A group of resources embedded under one relation; emitted as a single object or an array.</summary>
/// <param name="Relation">The relation the resources are embedded under.</param>
/// <param name="Resources">The embedded resource instances.</param>
/// <param name="Single">Whether to emit a single object (vs. an array) for this relation.</param>
public sealed record EmbeddedResource(LinkRelation Relation, IReadOnlyList<object> Resources, bool Single);
