namespace Cairn;

/// <summary>A hypermedia link relation type (the "rel"), such as <c>self</c> or <c>next</c>.</summary>
public readonly record struct LinkRelation
{
    /// <summary>Creates a relation from a token or URI.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null or whitespace.</exception>
    public LinkRelation(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Link relation must not be null or whitespace.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The relation token or URI.</summary>
    public string Value { get; }

    /// <summary>Creates a relation from a token or URI.</summary>
    public static LinkRelation FromString(string value) => new(value);

    /// <summary>Converts a string to a <see cref="LinkRelation"/>.</summary>
    public static implicit operator LinkRelation(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
