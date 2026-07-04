using System.Diagnostics.CodeAnalysis;

namespace Cairn;

/// <summary>An available action on a resource — a control describing how to invoke a state transition.</summary>
public sealed record Affordance
{
    /// <summary>Creates an affordance.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <c>default(LinkRelation)</c>, or <paramref name="href"/> or <paramref name="method"/> is null or whitespace.</exception>
    public Affordance(LinkRelation name, string href, string method)
    {
        name.ThrowIfDefault(nameof(name));
        if (string.IsNullOrWhiteSpace(href))
        {
            throw new ArgumentException("Affordance href must not be null or whitespace.", nameof(href));
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Affordance method must not be null or whitespace.", nameof(method));
        }

        Name = name;
        Href = href;
        Method = method;
    }

    /// <summary>The name identifying the action (the relation).</summary>
    public LinkRelation Name { get; init; }

    /// <summary>The target URI the action is invoked against.</summary>
    public string Href { get; init; }

    /// <summary>The HTTP method used to invoke the action.</summary>
    public string Method { get; init; }

    /// <summary>An optional human-readable title for the action.</summary>
    public string? Title { get; init; }

    /// <summary>An optional input type the action accepts, used to describe its form fields.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type? Input { get; init; }

    /// <summary>An optional content type the action's input is submitted as (HAL-FORMS <c>contentType</c>); defaults to <c>application/json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether this is the resource's primary action — emitted under the reserved <c>default</c> HAL-FORMS template key.</summary>
    public bool IsDefault { get; init; }
}
