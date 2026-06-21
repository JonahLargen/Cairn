namespace Cairn;

/// <summary>Commonly used IANA-registered link relation types.</summary>
public static class IanaLinkRelations
{
    /// <summary>An identifier for the link's context (<c>self</c>).</summary>
    public static readonly LinkRelation Self = new("self");

    /// <summary>The next resource in a series (<c>next</c>).</summary>
    public static readonly LinkRelation Next = new("next");

    /// <summary>The previous resource in a series (<c>prev</c>).</summary>
    public static readonly LinkRelation Prev = new("prev");

    /// <summary>The first resource in a series (<c>first</c>).</summary>
    public static readonly LinkRelation First = new("first");

    /// <summary>The last resource in a series (<c>last</c>).</summary>
    public static readonly LinkRelation Last = new("last");

    /// <summary>A member of a collection (<c>item</c>).</summary>
    public static readonly LinkRelation Item = new("item");

    /// <summary>The collection a resource belongs to (<c>collection</c>).</summary>
    public static readonly LinkRelation Collection = new("collection");
}
