using System.Diagnostics.CodeAnalysis;

namespace Cairn;

/// <summary>
/// Describes a link declaration a link configuration makes, so document generators (e.g. ALPS profile
/// documents) can describe the resource's possible transitions without building links for an instance.
/// Only statically declared metadata is reported — per-target overrides (<see cref="LinkTarget"/>
/// attributes) and the resolved URLs exist only at build time.
/// </summary>
/// <param name="Relation">The relation the link is declared under.</param>
public sealed record DeclaredLink(LinkRelation Relation)
{
    /// <summary>The declared human-readable title, if any.</summary>
    public string? Title { get; init; }

    /// <summary>The declared media type hint of the link's destination (RFC 8288 <c>type</c>), if any.</summary>
    public string? Type { get; init; }

    /// <summary>The declared secondary key (HAL/RFC 8288 <c>name</c>), if any.</summary>
    public string? Name { get; init; }

    /// <summary>The declared deprecation URL, if any.</summary>
    public string? Deprecation { get; init; }

    /// <summary>The declared language hint (RFC 8288 <c>hreflang</c>), if any.</summary>
    public string? Hreflang { get; init; }

    /// <summary>The declared profile URI of the link's destination (RFC 6906), if any.</summary>
    public string? Profile { get; init; }

    /// <summary>Whether the declaration yields multiple links under one relation (<c>Links</c> rather than <c>Link</c>/<c>Self</c>).</summary>
    public bool Multi { get; init; }

    /// <summary>Whether the link is gated by <c>When</c> or <c>RequireAuthorization</c>, so it may be absent from any given response.</summary>
    public bool Conditional { get; init; }
}

/// <summary>
/// Describes an affordance declaration a link configuration makes, so document generators (e.g. ALPS
/// profile documents) can describe the resource's possible actions without building them for an instance.
/// </summary>
/// <param name="Name">The name the affordance is declared under.</param>
/// <param name="HttpMethod">The HTTP method used to invoke the action.</param>
public sealed record DeclaredAffordance(LinkRelation Name, string HttpMethod)
{
    /// <summary>The declared human-readable title, if any.</summary>
    public string? Title { get; init; }

    /// <summary>The input type declared with <c>Accepts&lt;TInput&gt;</c>, if any.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type? InputType { get; init; }

    /// <summary>The declared submission content type (HAL-FORMS <c>contentType</c>), if any.</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether the affordance is marked as the resource's primary action (<c>AsDefault</c>).</summary>
    public bool IsDefault { get; init; }

    /// <summary>Whether the affordance is gated by <c>When</c> or <c>RequireAuthorization</c>, so it may be absent from any given response.</summary>
    public bool Conditional { get; init; }
}

/// <summary>
/// A compiled config that can report the link and affordance declarations it was built from. Document
/// generators (e.g. ALPS profile documents) query this — over <see cref="ICompiledLinkConfig"/> — to
/// describe a resource type's possible transitions; the runtime wire never needs it. Kept separate from
/// <see cref="ICompiledLinkConfig"/> so consumers that only build links are unaffected, mirroring
/// <see cref="IEmbeddedResourceReportingConfig"/>.
/// </summary>
public interface IDeclarationReportingConfig
{
    /// <summary>The link declarations made by the configuration, in declaration order.</summary>
    IReadOnlyList<DeclaredLink> DeclaredLinks { get; }

    /// <summary>The affordance declarations made by the configuration, in declaration order.</summary>
    IReadOnlyList<DeclaredAffordance> DeclaredAffordances { get; }
}
