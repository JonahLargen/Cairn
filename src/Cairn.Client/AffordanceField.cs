namespace Cairn.Client;

/// <summary>A field an affordance accepts, parsed from a HAL-FORMS template property.</summary>
public sealed record AffordanceField(string Name)
{
    /// <summary>Whether the field is required.</summary>
    public bool Required { get; init; }

    /// <summary>The field type (e.g. <c>text</c>, <c>number</c>, <c>email</c>, <c>checkbox</c>).</summary>
    public string? Type { get; init; }

    /// <summary>A regular expression the value must match.</summary>
    public string? Regex { get; init; }

    /// <summary>The maximum length of the value.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The minimum numeric value.</summary>
    public double? Min { get; init; }

    /// <summary>The maximum numeric value.</summary>
    public double? Max { get; init; }
}
