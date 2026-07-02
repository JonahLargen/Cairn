namespace Cairn;

/// <summary>Describes where a link or affordance points, resolved to a URL by the host.</summary>
public abstract record LinkTarget
{
    private protected LinkTarget()
    {
    }

    /// <summary>An optional per-link secondary key (HAL/RFC 8288 <c>name</c>) — HAL's way to disambiguate members of a link array. Overrides the spec-level value.</summary>
    public string? Name { get; init; }

    /// <summary>An optional per-link human-readable title. Overrides the spec-level value.</summary>
    public string? Title { get; init; }

    /// <summary>An optional per-link media type hint (RFC 8288 <c>type</c>). Overrides the spec-level value.</summary>
    public string? Type { get; init; }

    /// <summary>An optional per-link language hint (RFC 8288 <c>hreflang</c>). Overrides the spec-level value.</summary>
    public string? Hreflang { get; init; }

    /// <summary>An optional per-link deprecation URL. Overrides the spec-level value.</summary>
    public string? Deprecation { get; init; }

    /// <summary>An optional per-link profile URI (RFC 6906 <c>profile</c>). Overrides the spec-level value.</summary>
    public string? Profile { get; init; }

    /// <summary>Targets a named route, optionally with route values.</summary>
    public static LinkTarget Route(string routeName, object? routeValues = null) => new RouteLinkTarget(routeName, routeValues);

    /// <summary>Targets an explicit URI (or URI template).</summary>
    public static LinkTarget Uri(string href, bool templated = false) => new ExplicitLinkTarget(href, templated);

    /// <summary>Returns a copy with the per-link <c>name</c> set — useful to disambiguate members of a multi-link relation.</summary>
    public LinkTarget WithName(string name) => this with { Name = name };

    /// <summary>Returns a copy with the per-link title set.</summary>
    public LinkTarget WithTitle(string title) => this with { Title = title };

    /// <summary>Returns a copy with the per-link media type hint set.</summary>
    public LinkTarget WithType(string mediaType) => this with { Type = mediaType };

    /// <summary>Returns a copy with the per-link <c>hreflang</c> set.</summary>
    public LinkTarget WithHreflang(string language) => this with { Hreflang = language };

    /// <summary>Returns a copy marked deprecated; <paramref name="url"/> should point at information about the deprecation.</summary>
    public LinkTarget WithDeprecation(string url) => this with { Deprecation = url };

    /// <summary>Returns a copy with the per-link profile URI set.</summary>
    public LinkTarget WithProfile(string profileUri) => this with { Profile = profileUri };
}

/// <summary>A target identified by a named route and optional route values.</summary>
public sealed record RouteLinkTarget(string RouteName, object? RouteValues) : LinkTarget;

/// <summary>A target identified by an explicit URI.</summary>
public sealed record ExplicitLinkTarget(string Href, bool Templated) : LinkTarget;
