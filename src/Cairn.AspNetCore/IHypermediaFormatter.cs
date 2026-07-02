namespace Cairn.AspNetCore;

/// <summary>
/// Projects a resource's computed hypermedia into a custom wire format (e.g. Siren, JSON:API, Collection+JSON),
/// opening the built-in <see cref="HypermediaFormat"/> set. Register with
/// <see cref="CairnOptions.AddFormatter"/>; the format is selected when the request's <c>Accept</c> header
/// names <see cref="MediaType"/>, or forced per endpoint with <c>WithHypermediaFormat("media/type")</c>.
/// When active, the formatter's <see cref="Properties"/> are injected instead of the built-in
/// <c>_links</c>/<c>_actions</c>/<c>_templates</c>, and the response's <c>application/json</c> content type
/// is relabeled to <see cref="MediaType"/>.
/// </summary>
public interface IHypermediaFormatter
{
    /// <summary>The media type this format is negotiated by and emitted as (e.g. <c>application/vnd.siren+json</c>).</summary>
    string MediaType { get; }

    /// <summary>
    /// The JSON properties this format injects into each linked resource. Read once at startup — the set of
    /// property names must be fixed, but each property's value is computed per resource at serialization time.
    /// </summary>
    IReadOnlyList<HypermediaFormatProperty> Properties { get; }
}

/// <summary>
/// One JSON property a <see cref="IHypermediaFormatter"/> injects: its <paramref name="Name"/> on the wire and
/// the <paramref name="Value"/> projecting a resource's hypermedia into the serialized value (return
/// <see langword="null"/> to omit the property). The returned object is serialized with the app's JSON options,
/// so pin wire names (attributes or string-keyed dictionaries) rather than relying on a naming policy.
/// </summary>
/// <param name="Name">The JSON property name (e.g. <c>entities</c>).</param>
/// <param name="Value">Projects the resource's hypermedia into the property's value.</param>
public sealed record HypermediaFormatProperty(string Name, Func<HypermediaDocument, object?> Value);

/// <summary>The hypermedia computed for a single resource, as handed to a <see cref="IHypermediaFormatter"/>.</summary>
public sealed class HypermediaDocument
{
    internal HypermediaDocument(IReadOnlyList<Link> links, IReadOnlyList<Affordance> affordances, IReadOnlyDictionary<string, object>? embedded)
    {
        Links = links;
        Affordances = affordances;
        Embedded = embedded;
    }

    /// <summary>The resource's links, including any pagination links, in declaration order.</summary>
    public IReadOnlyList<Link> Links { get; }

    /// <summary>The resource's affordances. <see cref="Affordance.Input"/> carries the declared input type, if any.</summary>
    public IReadOnlyList<Affordance> Affordances { get; }

    /// <summary>The resources embedded under this one, keyed by relation (a single object or a list per relation).</summary>
    public IReadOnlyDictionary<string, object>? Embedded { get; }
}
