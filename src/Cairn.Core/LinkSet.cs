namespace Cairn;

/// <summary>The links and affordances computed for a single resource.</summary>
public sealed class LinkSet
{
    /// <summary>An empty set with no links or affordances.</summary>
    public static readonly LinkSet Empty = new([], []);

    /// <summary>Creates a link set.</summary>
    public LinkSet(IReadOnlyList<Link> links, IReadOnlyList<Affordance> affordances)
    {
        Links = links;
        Affordances = affordances;
    }

    /// <summary>The resource's links.</summary>
    public IReadOnlyList<Link> Links { get; }

    /// <summary>The resource's affordances (available actions).</summary>
    public IReadOnlyList<Affordance> Affordances { get; }

    /// <summary>Whether the set contains no links and no affordances.</summary>
    public bool IsEmpty => Links.Count == 0 && Affordances.Count == 0;
}
