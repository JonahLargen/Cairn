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
}
