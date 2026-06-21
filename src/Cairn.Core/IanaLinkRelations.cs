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

    /// <summary>A related resource (<c>related</c>).</summary>
    public static readonly LinkRelation Related = new("related");

    /// <summary>A parent resource in a hierarchy (<c>up</c>).</summary>
    public static readonly LinkRelation Up = new("up");

    /// <summary>A resource that can be used to edit the context (<c>edit</c>).</summary>
    public static readonly LinkRelation Edit = new("edit");

    /// <summary>A form used to edit the context (<c>edit-form</c>).</summary>
    public static readonly LinkRelation EditForm = new("edit-form");

    /// <summary>A form used to create a new resource (<c>create-form</c>).</summary>
    public static readonly LinkRelation CreateForm = new("create-form");

    /// <summary>A resource that provides information about the context (<c>about</c>).</summary>
    public static readonly LinkRelation About = new("about");

    /// <summary>A resource describing the context (<c>describedby</c>).</summary>
    public static readonly LinkRelation DescribedBy = new("describedby");

    /// <summary>A resource the context describes (<c>describes</c>).</summary>
    public static readonly LinkRelation Describes = new("describes");

    /// <summary>A resource that can be used to search (<c>search</c>).</summary>
    public static readonly LinkRelation Search = new("search");

    /// <summary>An alternate representation of the context (<c>alternate</c>).</summary>
    public static readonly LinkRelation Alternate = new("alternate");

    /// <summary>The preferred (canonical) URI of the context (<c>canonical</c>).</summary>
    public static readonly LinkRelation Canonical = new("canonical");

    /// <summary>The latest version of the context (<c>latest-version</c>).</summary>
    public static readonly LinkRelation LatestVersion = new("latest-version");

    /// <summary>The starting resource of an application (<c>start</c>).</summary>
    public static readonly LinkRelation Start = new("start");

    /// <summary>Context-sensitive help (<c>help</c>).</summary>
    public static readonly LinkRelation Help = new("help");

    /// <summary>A license for the context (<c>license</c>).</summary>
    public static readonly LinkRelation License = new("license");

    /// <summary>The author of the context (<c>author</c>).</summary>
    public static readonly LinkRelation Author = new("author");

    /// <summary>The status of the context (<c>status</c>).</summary>
    public static readonly LinkRelation Status = new("status");
}
