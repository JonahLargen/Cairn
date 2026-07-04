namespace Cairn.AspNetCore;

/// <summary>The wire format Cairn projects hypermedia into.</summary>
public enum HypermediaFormat
{
    /// <summary>Cairn's default shape: <c>_links</c> plus <c>_actions</c>.</summary>
    Default,

    /// <summary>HAL (<c>application/hal+json</c>): <c>_links</c> only. Affordances are not emitted.</summary>
    Hal,

    /// <summary>HAL-FORMS (<c>application/prs.hal-forms+json</c>): <c>_links</c> plus <c>_templates</c> for affordances.</summary>
    HalForms,

    /// <summary>
    /// No hypermedia: the resource serializes exactly as its DTO declares, with no injected properties. Set as
    /// <see cref="CairnOptions.DefaultFormat"/> to make hypermedia <em>opt-in by the client</em> — a plain
    /// <c>application/json</c> (or wildcard) request gets the bare resource, and links are emitted only when the
    /// <c>Accept</c> header explicitly names a hypermedia media type (<c>application/hal+json</c>,
    /// <c>application/prs.hal-forms+json</c>, or a registered custom formatter's). Can also be forced per
    /// endpoint or route group with <c>WithHypermediaFormat(HypermediaFormat.None)</c> to suppress links there.
    /// </summary>
    None,
}
