namespace Cairn;

/// <summary>
/// Declares that a HAL-FORMS field's selectable values come from a remote resource — <em>options by
/// reference</em> — instead of an inline list. Apply it to a property of an affordance's input type
/// (<see cref="IAffordanceSpec{T}.Accepts{TInput}"/>); the field's <c>options</c> block is then emitted with a
/// <c>link</c> whose <c>href</c> a client dereferences to fetch the value list, rather than the inline
/// enumeration derived from an <c>enum</c> or <c>bool</c> property.
/// </summary>
/// <remarks>
/// HAL-FORMS <c>options</c> carry either an inline list or a link, never both, so this attribute takes
/// precedence over the automatic inline derivation. <see cref="Href"/> is emitted verbatim (a relative or
/// absolute URI, or a URI template when <see cref="Templated"/> is set) — it is not resolved through the
/// route table, because the field schema is computed once per input type and reused across requests.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class HalFormsOptionsLinkAttribute : Attribute
{
    /// <summary>Declares that the field's options are fetched from <paramref name="href"/>.</summary>
    /// <param name="href">The URI a client dereferences to fetch the field's selectable values (the link's <c>href</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="href"/> is null or whitespace.</exception>
    public HalFormsOptionsLinkAttribute(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            throw new ArgumentException("Options link href must not be null or whitespace.", nameof(href));
        }

        Href = href;
    }

    /// <summary>The URI a client dereferences to fetch the field's selectable values (the link's <c>href</c>).</summary>
    public string Href { get; }

    /// <summary>Whether <see cref="Href"/> is a URI template the client expands before dereferencing (the link's <c>templated</c>); omitted when <see langword="false"/>.</summary>
    public bool Templated { get; set; }

    /// <summary>An optional media-type hint for the linked resource (the link's <c>type</c>).</summary>
    public string? Type { get; set; }

    /// <summary>The field of each fetched item whose value is submitted (HAL-FORMS <c>valueField</c>); omitted when unset.</summary>
    public string? ValueField { get; set; }

    /// <summary>The field of each fetched item shown to the user (HAL-FORMS <c>promptField</c>); omitted when unset.</summary>
    public string? PromptField { get; set; }
}
