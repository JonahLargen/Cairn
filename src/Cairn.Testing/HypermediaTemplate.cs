namespace Cairn.Testing;

/// <summary>A HAL-FORMS template parsed from a resource's <c>_templates</c>.</summary>
/// <param name="Target">The URI the completed form is submitted to. When the wire form omits <c>target</c>, this falls back to the resource's <c>self</c> link, as HAL-FORMS prescribes.</param>
/// <param name="Method">The HTTP method used to submit the form.</param>
/// <param name="Title">An optional human-readable title.</param>
public sealed record HypermediaTemplate(string Target, string Method, string? Title)
{
    /// <summary>The media type the form is submitted as (HAL-FORMS <c>contentType</c>).</summary>
    public string? ContentType { get; init; }

    /// <summary>The template's fields (HAL-FORMS <c>properties</c>), in document order.</summary>
    public IReadOnlyList<HypermediaTemplateField> Fields { get; init; } = [];
}

/// <summary>A single field of a HAL-FORMS template (one entry of its <c>properties</c> array).</summary>
/// <param name="Name">The field's name.</param>
public sealed record HypermediaTemplateField(string Name)
{
    /// <summary>Whether the field must be supplied (HAL-FORMS <c>required</c>; omitted on the wire means optional).</summary>
    public bool Required { get; init; }

    /// <summary>Whether the field is read-only (HAL-FORMS <c>readOnly</c>).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The field's input type (e.g. <c>text</c>, <c>number</c>, <c>email</c>).</summary>
    public string? Type { get; init; }

    /// <summary>A regular expression the field's value must match (HAL-FORMS <c>regex</c>).</summary>
    public string? Regex { get; init; }

    /// <summary>A human-readable prompt for the field.</summary>
    public string? Prompt { get; init; }

    /// <summary>Placeholder text shown while the field is empty.</summary>
    public string? Placeholder { get; init; }

    /// <summary>The field's default value (HAL-FORMS <c>value</c>).</summary>
    public string? Value { get; init; }

    /// <summary>The maximum length of the field's value.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The minimum numeric value.</summary>
    public double? Min { get; init; }

    /// <summary>The maximum numeric value.</summary>
    public double? Max { get; init; }

    /// <summary>The field's selectable values (HAL-FORMS <c>options.inline</c> values); empty when the field is free-form.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
}
