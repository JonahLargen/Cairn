# Link configurations

A link configuration declares the links and affordances for a single resource type by subclassing `LinkConfig<T>` and overriding `Configure(ILinkBuilder<T>)`. The engine runs your configuration per response, evaluating conditions and resolving targets against the current request.

```csharp
public sealed class OrderLinks : LinkConfig<Order>
{
    public override void Configure(ILinkBuilder<Order> builder)
    {
        builder.Self(o => LinkTarget.Route("GetOrder", new { id = o.Id }));
        builder.Link(IanaLinkRelations.Collection, _ => LinkTarget.Route("ListOrders"));
    }
}
```

Register configurations through `CairnOptions` — see [getting-started.md](getting-started.md).

## The builder surface

`ILinkBuilder<T>` exposes the full declaration vocabulary:

| Method | Purpose |
| --- | --- |
| `Self(...)` | The `self` link. |
| `Link(relation, ...)` | A single link with a relation. |
| `Links(relation, ...)` | Multiple links sharing one relation, emitted as a HAL link array. |
| `Affordance(name, ...)` | An available action — see [affordances-and-forms.md](affordances-and-forms.md). |
| `Embed<TChild>(relation, ...)` | Embeds a single related resource — see [embedded-resources.md](embedded-resources.md). |
| `EmbedMany<TChild>(relation, ...)` | Embeds a collection of related resources. |

`Self`, `Link`, and `Affordance` each have three overloads: a plain `Func<T, LinkTarget>`, a service-aware `Func<T, LinkContext, LinkTarget>`, and an async `Func<T, LinkContext, ValueTask<LinkTarget>>`.

## Self and Link

`Self` adds the `self` link; `Link` adds a link under any relation. Both return an `ILinkSpec<T>` for per-link configuration.

```csharp
public override void Configure(ILinkBuilder<Order> builder)
{
    builder.Self(o => LinkTarget.Route("GetOrder", new { id = o.Id }));

    builder.Link(IanaLinkRelations.Edit, o => LinkTarget.Route("UpdateOrder", new { id = o.Id }))
        .Title("Edit this order");

    builder.Link("invoice", o => LinkTarget.Route("GetInvoice", new { id = o.Id }));
}
```

The relation argument is a `LinkRelation`. A `string` converts implicitly (`"invoice"` above), or use one of the `IanaLinkRelations` constants.

## Links (HAL link arrays)

`Links` adds several links under one relation, projected from the resource. They are emitted as an array under that relation (for example, one `item` link per child).

```csharp
builder.Links(
    IanaLinkRelations.Item,
    o => o.LineItems.Select(li => LinkTarget.Route("GetLineItem", new { id = li.Id })));
```

A service-aware async overload is available when the targets depend on request services:

```csharp
ILinkSpec<T> Links(LinkRelation relation, Func<T, LinkContext, ValueTask<IEnumerable<LinkTarget>>> targets);
```

## Link targets: Route vs Uri

A `LinkTarget` describes where a link points; the host resolves it to a URL.

- `LinkTarget.Route(string routeName, object? routeValues = null)` — points at a named route, optionally with route values. Resolution honors the configured route, so the URL stays correct if the route template changes.
- `LinkTarget.Uri(string href, bool templated = false)` — points at an explicit URI. Set `templated: true` to emit an RFC 6570 URI template.

```csharp
builder.Self(o => LinkTarget.Route("GetOrder", new { id = o.Id }));

builder.Link("docs", _ => LinkTarget.Uri("https://example.com/docs"));

builder.Link(IanaLinkRelations.Search,
    _ => LinkTarget.Uri("/orders{?status,page}", templated: true));
```

A templated target produces a `Link` whose `Templated` flag is `true` on the wire.

## Per-link attributes

`ILinkSpec<T>` decorates a single link. Each method returns the spec, so calls chain.

| Method | Effect |
| --- | --- |
| `Title(string title)` | Human-readable title. |
| `Type(string mediaType)` | Media type hint for the destination (RFC 8288 `type`). |
| `Name(string name)` | Secondary key for selecting between links that share a relation (HAL/RFC 8288 `name`). |
| `Deprecated(string url)` | Marks the link deprecated; `url` should point at information about the deprecation. |
| `Hreflang(string language)` | Language hint (RFC 8288 `hreflang`). |
| `Profile(string profileUri)` | Profile URI describing the destination (RFC 6906 `profile`). |

```csharp
builder.Link(IanaLinkRelations.Alternate, o => LinkTarget.Route("GetOrderPdf", new { id = o.Id }))
    .Title("Printable invoice")
    .Type("application/pdf")
    .Hreflang("en");

builder.Link("legacy-status", o => LinkTarget.Route("GetStatus", new { id = o.Id }))
    .Deprecated("https://example.com/deprecations/legacy-status");
```

## Conditions

Conditions decide whether a link is included for a given resource and request. They compose: a link is emitted only when every condition holds.

### When

`When` has three overloads on `ILinkSpec<T>`:

```csharp
ILinkSpec<T> When(Func<T, bool> condition);                          // resource only
ILinkSpec<T> When(Func<T, LinkContext, bool> condition);             // service-aware, sync
ILinkSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition);  // service-aware, async
```

```csharp
builder.Link(IanaLinkRelations.Edit, o => LinkTarget.Route("UpdateOrder", new { id = o.Id }))
    .When(o => o.Status == OrderStatus.Open);

builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id }))
    .When(o => !o.IsCancelled);
```

`IAffordanceSpec<T>` exposes the same three `When` overloads.

### RequireAuthorization

`RequireAuthorization` gates a link on authorization:

```csharp
ILinkSpec<T> RequireAuthorization(string policy);  // named policy
ILinkSpec<T> RequireAuthorization();               // default policy (an authenticated user, by default)
```

```csharp
builder.Affordance(IanaLinkRelations.Edit, o => LinkTarget.Route("UpdateOrder", new { id = o.Id }))
    .RequireAuthorization("CanEditOrders");

builder.Link("audit-log", o => LinkTarget.Route("GetOrderAudit", new { id = o.Id }))
    .RequireAuthorization();
```

Authorization gating uses the engine's `ILinkAuthorizer`, which evaluates the policy against the current caller. The default policy admits an authenticated caller. Authorization-gated affordances also show up on controllers — see [controllers.md](controllers.md) — and a denied transition surfaces as a problem response — see [error-responses.md](error-responses.md).

## Service-aware targets and conditions

The service-aware overloads pass a `LinkContext`, which exposes:

- `Services` — the request's `IServiceProvider`, for data not on the DTO.
- `CancellationToken` — the request's cancellation token.

Resolve dependencies from `ctx.Services` to compute a target or evaluate a condition:

```csharp
builder.Link("recommendations", (o, ctx) => LinkTarget.Route("GetRecommendations", new { id = o.Id }))
    .When((o, ctx) =>
    {
        var flags = ctx.Services.GetRequiredService<IFeatureFlags>();
        return flags.IsEnabled("recommendations");
    });
```

### Avoiding N+1 with a batched holder

A service-aware condition or target runs once per resource. If it queries a backing store per item, a collection response fans out into N queries. Load the data once into a request-scoped holder and read from it in each condition instead.

```csharp
// Request-scoped holder, populated once before resources are decorated.
public sealed class OrderPermissions
{
    private readonly HashSet<int> _editable = new();

    public void Allow(int orderId) => _editable.Add(orderId);
    public bool CanEdit(int orderId) => _editable.Contains(orderId);
}
```

```csharp
public override void Configure(ILinkBuilder<Order> builder)
{
    builder.Self(o => LinkTarget.Route("GetOrder", new { id = o.Id }));

    builder.Affordance(IanaLinkRelations.Edit, o => LinkTarget.Route("UpdateOrder", new { id = o.Id }))
        .When((o, ctx) =>
        {
            var perms = ctx.Services.GetRequiredService<OrderPermissions>();
            return perms.CanEdit(o.Id);
        });
}
```

The handler populates the holder in a single batched call (for example, one query for the whole page) before returning the resources. Each `When` then reads from the in-memory holder, so the per-item cost is a set lookup rather than a round trip. Pass `ctx.CancellationToken` to any async work the holder still performs.

## IanaLinkRelations

`IanaLinkRelations` provides constants for commonly used IANA-registered relations, so the relation token is spelled once and consistently:

`Self`, `Next`, `Prev`, `First`, `Last`, `Item`, `Collection`, `Related`, `Up`, `Edit`, `EditForm`, `CreateForm`, `About`, `DescribedBy`, `Describes`, `Search`, `Alternate`, `Canonical`, `LatestVersion`, `Start`, `Help`, `License`, `Author`, `Status`.

```csharp
builder.Link(IanaLinkRelations.Next, o => LinkTarget.Route("ListOrders", new { page = o.Page + 1 }));
```

For any relation not in the list, pass a `string` (it converts to `LinkRelation` implicitly) or a custom relation URI.

## Related pages

- [formats.md](formats.md) — how configured links render across Default, HAL, and HAL-FORMS.
- [affordances-and-forms.md](affordances-and-forms.md) — affordances, methods, and form fields.
- [embedded-resources.md](embedded-resources.md) — `Embed`, `EmbedMany`, link arrays, and CURIEs.
- [pagination.md](pagination.md) — `next`/`prev`/`first`/`last` for paged collections.
