# Embedded resources, link arrays & CURIEs

A resource often carries more than one link per relation, embeds related resources inline, or references custom relations documented elsewhere. This page covers the three constructs that handle those cases: `Embed`/`EmbedMany`, the `Links` link array, and CURIEs. They build on the [link configuration builder](link-configs.md) and surface in the [HAL and HAL-FORMS wire formats](formats.md).

## Embedded resources (HAL `_embedded`)

`Embed` and `EmbedMany` attach related resources inline under HAL `_embedded`. Each embedded resource is decorated by its own `LinkConfig<TChild>` — registered the same way as the parent's config — so embedding composes configs rather than duplicating link logic.

### A single embedded resource

`Embed<TChild>(LinkRelation relation, Func<T, TChild?> resource)` embeds one related resource, where `TChild` is a reference type. A null result embeds nothing for that relation; otherwise the relation emits a single object.

```csharp
public sealed record Order(int Id, Customer Customer);
public sealed record Customer(int Id, string Name);

public sealed class OrderLinks : LinkConfig<Order>
{
    public override void Configure(ILinkBuilder<Order> builder)
    {
        builder.Self(o => LinkTarget.Route("orders.get", new { id = o.Id }));
        builder.Embed("customer", o => o.Customer);
    }
}

public sealed class CustomerLinks : LinkConfig<Customer>
{
    public override void Configure(ILinkBuilder<Customer> builder)
    {
        builder.Self(c => LinkTarget.Route("customers.get", new { id = c.Id }));
    }
}
```

```json
{
  "_links": { "self": { "href": "/orders/42" } },
  "_embedded": {
    "customer": {
      "id": 7,
      "name": "Ada",
      "_links": { "self": { "href": "/customers/7" } }
    }
  }
}
```

### Many embedded resources

`EmbedMany<TChild>(LinkRelation relation, Func<T, IEnumerable<TChild>?> resources)` embeds a collection. The relation is always emitted as an array, even when it contains zero or one item. Each item is decorated by its own `LinkConfig<TChild>`. A null `resources` result embeds nothing for the relation.

```csharp
public sealed record Order(int Id, IReadOnlyList<LineItem> Items);
public sealed record LineItem(int Id, string Sku);

public sealed class OrderLinks : LinkConfig<Order>
{
    public override void Configure(ILinkBuilder<Order> builder)
    {
        builder.Self(o => LinkTarget.Route("orders.get", new { id = o.Id }));
        builder.EmbedMany("items", o => o.Items);
    }
}

public sealed class LineItemLinks : LinkConfig<LineItem>
{
    public override void Configure(ILinkBuilder<LineItem> builder)
    {
        builder.Self(i => LinkTarget.Route("lineitems.get", new { id = i.Id }));
    }
}
```

```json
{
  "_links": { "self": { "href": "/orders/42" } },
  "_embedded": {
    "items": [
      { "id": 1, "sku": "A-1", "_links": { "self": { "href": "/lineitems/1" } } },
      { "id": 2, "sku": "B-2", "_links": { "self": { "href": "/lineitems/2" } } }
    ]
  }
}
```

## Multiple links per relation (link arrays)

`Links(LinkRelation relation, Func<T, IEnumerable<LinkTarget>> targets)` emits several links that share one relation, as a HAL link array. Use it to point one relation at many destinations — for example one `item` link per child.

```csharp
public sealed class OrderLinks : LinkConfig<Order>
{
    public override void Configure(ILinkBuilder<Order> builder)
    {
        builder.Self(o => LinkTarget.Route("orders.get", new { id = o.Id }));
        builder.Links(
            "item",
            o => o.Items.Select(i => LinkTarget.Route("lineitems.get", new { id = i.Id })));
    }
}
```

```json
{
  "_links": {
    "self": { "href": "/orders/42" },
    "item": [
      { "href": "/lineitems/1" },
      { "href": "/lineitems/2" }
    ]
  }
}
```

A service-aware overload computes the targets with access to the request's services:

```csharp
ILinkSpec<T> Links(LinkRelation relation, Func<T, LinkContext, ValueTask<IEnumerable<LinkTarget>>> targets);
```

Declaring a relation more than once also yields an array: repeated `Link` (or `Links`) calls for the same relation merge into a single array under that relation. Use `Links` when one call already yields all targets, and repeated `Link` calls when the targets come from distinct expressions. The `Name` spec sets a secondary key for selecting between links that share a relation (HAL/RFC 8288 `name`).

```csharp
builder.Link("alternate", o => LinkTarget.Route("orders.pdf", new { id = o.Id })).Name("pdf");
builder.Link("alternate", o => LinkTarget.Route("orders.csv", new { id = o.Id })).Name("csv");
```

## CURIEs

A CURIE (compact URI) names a documentation prefix and a templated href so custom relations stay short while remaining resolvable to their documentation. Register one with `AddCurie(string prefix, string hrefTemplate)` — the template must contain the `{rel}` variable (curies are advertised `templated: true`, and clients expand the relation's suffix into it). An `hrefTemplate` missing `{rel}` fails fast with an `ArgumentException` at registration, so the always-`templated: true` advertisement can never be a lie:

```csharp
builder.Services.AddCairn(options =>
{
    options.AddLinksFromAssemblyContaining<Program>();
    options.AddCurie("acme", "https://docs.example.com/rels/{rel}");
});
```

When a resource uses a custom relation carrying that prefix (for example `acme:widget`), Cairn surfaces the matching curie in `_links.curies` so clients can expand `acme:widget` into its documentation URL:

```csharp
public sealed class GadgetLinks : LinkConfig<Gadget>
{
    public override void Configure(ILinkBuilder<Gadget> builder)
    {
        builder.Self(g => LinkTarget.Route("gadgets.get", new { id = g.Id }));
        builder.Link("acme:widget", g => LinkTarget.Route("widgets.get", new { id = g.WidgetId }));
    }
}
```

```json
{
  "_links": {
    "curies": [
      { "name": "acme", "href": "https://docs.example.com/rels/{rel}", "templated": true }
    ],
    "self": { "href": "/gadgets/3" },
    "acme:widget": { "href": "/widgets/9" }
  }
}
```

A registered prefix appears in `_links.curies` only for a resource that uses a relation with that prefix — and any rel-keyed section counts, not just `_links`: an affordance named `acme:reorder` (surfacing in `_actions`/`_templates`) or an embedded relation `acme:child` (surfacing in `_embedded`) also brings the `acme` curie into `_links.curies`. `_links.curies` is a HAL construct, so it surfaces in the HAL and HAL-FORMS [wire formats](formats.md) — with two refinements: HAL never emits affordances, so a prefix used *only* by affordance names is not advertised in a HAL response (nothing in that document would carry it); and in HAL-FORMS an affordance emitted under the reserved `default` template key ([`AsDefault()`](affordances-and-forms.md#the-default-template-asdefault), or a response's sole template) doesn't advertise its prefix either, since its curie'd name never appears on the wire.

Relation keys — including CURIE prefixes — compare case-insensitively per RFC 8288, so `acme:Widget` and `acme:widget` group under one key (the first-declared casing), and they are emitted verbatim regardless of the host's JSON dictionary-key policy.

## See also

- [Link configurations](link-configs.md) — the builder, conditions, service-aware targets, and authorization.
- [Wire formats & negotiation](formats.md) — how `_embedded`, link arrays, and `curies` render per format.
- [The typed client](client.md) — reading embedded resources with `Embedded<TChild>(rel)` and link arrays with `LinksFor(rel)`.
