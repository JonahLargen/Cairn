namespace Cairn.Client;

/// <summary>A selectable value for a field (a HAL-FORMS <c>options.inline</c> entry).</summary>
/// <param name="Value">The value submitted when this option is chosen.</param>
public sealed record AffordanceFieldOption(string Value)
{
    /// <summary>A human-readable label for the option (HAL-FORMS <c>prompt</c>); falls back to <see cref="Value"/> when absent.</summary>
    public string? Prompt { get; init; }
}

/// <summary>A field an affordance accepts, parsed from a HAL-FORMS template property.</summary>
public sealed record AffordanceField(string Name)
{
    /// <summary>A human-readable label for the field (HAL-FORMS <c>prompt</c>).</summary>
    public string? Prompt { get; init; }

    /// <summary>Whether the field is required.</summary>
    public bool Required { get; init; }

    /// <summary>Whether the field is read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The field type (e.g. <c>text</c>, <c>number</c>, <c>email</c>, <c>checkbox</c>).</summary>
    public string? Type { get; init; }

    /// <summary>The field's current or default value (HAL-FORMS <c>value</c>).</summary>
    public string? Value { get; init; }

    /// <summary>Whether <see cref="Value"/> contains a URI template (HAL-FORMS property-level <c>templated</c>).</summary>
    public bool Templated { get; init; }

    /// <summary>A placeholder hint shown in the empty field.</summary>
    public string? Placeholder { get; init; }

    /// <summary>A regular expression the value must match.</summary>
    public string? Regex { get; init; }

    /// <summary>The minimum length of the value.</summary>
    public int? MinLength { get; init; }

    /// <summary>The maximum length of the value.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The minimum numeric value.</summary>
    public double? Min { get; init; }

    /// <summary>The maximum numeric value.</summary>
    public double? Max { get; init; }

    /// <summary>The granularity of a numeric value (HAL-FORMS <c>step</c>).</summary>
    public double? Step { get; init; }

    /// <summary>The visible width of a textarea field, in columns (HAL-FORMS <c>cols</c>).</summary>
    public int? Cols { get; init; }

    /// <summary>The visible height of a textarea field, in rows (HAL-FORMS <c>rows</c>).</summary>
    public int? Rows { get; init; }

    /// <summary>The selectable values for the field (HAL-FORMS <c>options.inline</c>), if any.</summary>
    public IReadOnlyList<AffordanceFieldOption> Options { get; init; } = [];

    /// <summary>The href of an external list of selectable values (HAL-FORMS <c>options.link</c>), if any.</summary>
    public string? OptionsLink { get; init; }

    /// <summary>The values pre-selected among the field's options (HAL-FORMS <c>options.selectedValues</c>).</summary>
    public IReadOnlyList<string> SelectedValues { get; init; } = [];
}
