# Custom wire formats

The built-in formats (Default, HAL, HAL-FORMS) cover the common cases, but hypermedia has more dialects — Siren, Collection+JSON, a house format your clients already speak. An `IHypermediaFormatter` plugs such a format into the same pipeline: its media type participates in `Accept` negotiation alongside the built-ins, and it can be forced per endpoint.

## The interface

```csharp
public interface IHypermediaFormatter
{
    string MediaType { get; }
    IReadOnlyList<HypermediaFormatProperty> Properties { get; }
}

public sealed record HypermediaFormatProperty(string Name, Func<HypermediaDocument, object?> Value);
```

A formatter declares the JSON properties it injects (`Properties`) and, per resource, projects a `HypermediaDocument` — the computed `Links`, `Affordances` (whose `Input` carries the declared input type), and `Embedded` children — into each property's value. Returning `null` from a property's projection omits it for that resource.

When a formatter is active it **supersedes** the built-in properties: the resource gets the formatter's properties instead of `_links`/`_actions`/`_templates`, and the response's `application/json` content type is relabeled to the formatter's `MediaType`.

## Registering and selecting

```csharp
// A miniature Siren projection: links as [{rel: [..], href}], affordances as [{name, method, href}].
public sealed class SirenFormatter : IHypermediaFormatter
{
    public string MediaType => "application/vnd.siren+json";

    public IReadOnlyList<HypermediaFormatProperty> Properties { get; } =
    [
        new("links", document => document.Links.Count == 0
            ? null
            : document.Links.Select(link => new { rel = new[] { link.Relation.Value }, href = link.Href })),

        new("actions", document => document.Affordances.Count == 0
            ? null
            : document.Affordances.Select(a => new { name = a.Name.Value, method = a.Method, href = a.Href })),
    ];
}

builder.Services.AddCairn(o => o.AddFormatter(new SirenFormatter()));
```

From there the format is selected like any other — see [formats.md](formats.md) for the full precedence:

- **Negotiation**: a request with `Accept: application/vnd.siren+json` gets the Siren shape (standard q-value rules apply; a higher-quality built-in type still wins).
- **Per endpoint**: `.WithHypermediaFormat("application/vnd.siren+json")` forces it regardless of `Accept`. Forcing a media type no formatter registered throws an `InvalidOperationException` at request time.

`Properties` is read once at startup, so the property names are fixed for the formatter's lifetime; the value delegates run per resource. Registering two formatters with the same media type throws an `ArgumentException`.

## Related

- [Wire formats & negotiation](formats.md) — the built-in formats and resolution precedence.
- [Affordances & HAL-FORMS](affordances-and-forms.md) — what the `Affordances` in a `HypermediaDocument` carry.
