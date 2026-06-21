# Controllers (MVC)

Cairn works with MVC controllers at full parity with minimal APIs. The same `LinkConfig<T>` drives both: declare hypermedia once against your DTO and opt in per action (or per controller) with `[CairnLinks]`. Formats, pagination, embedded resources, and forms behave identically — see [Getting started](getting-started.md) for the shared model.

## Registration

`AddCairn` registers the link engine and a JSON modifier on both serialization paths. The modifier is added to `Microsoft.AspNetCore.Http.JsonOptions` (minimal APIs and `WriteAsJsonAsync`) and to `Microsoft.AspNetCore.Mvc.JsonOptions` (the MVC `System.Text.Json` output formatter). Configuring the MVC path is harmless when MVC is not in use, so a single call covers both.

```csharp
builder.Services.AddControllers();

builder.Services.AddCairn(o =>
{
    o.AddLinks(new CustomerLinks());
});

var app = builder.Build();
app.MapControllers();
app.Run();
```

## Opting in with `[CairnLinks]`

`CairnLinksAttribute` is the controller counterpart to `WithLinks()`. Apply it to an action or to a controller class (`AttributeUsage` targets `Class | Method`, and it is `Inherited`). The returned value — and each element of a returned collection — is linked according to its runtime type's configuration. Actions without the attribute are serialized unchanged.

```csharp
[ApiController]
[Route("customers")]
public sealed class CustomersController : ControllerBase
{
    [HttpGet("{id:int}", Name = "GetCustomerById")]
    [CairnLinks]
    public CustomerDto Get(int id) => new(id, "Acme Corp");
}
```

The DTO and its `LinkConfig<T>` are identical to what a minimal API would use:

```csharp
public record CustomerDto(int Id, string Name);

public sealed class CustomerLinks : LinkConfig<CustomerDto>
{
    public override void Configure(ILinkBuilder<CustomerDto> builder)
        => builder.Self(customer => LinkTarget.Route("GetCustomerById", new { id = customer.Id }));
}
```

The response carries the projected hypermedia without changing the DTO shape:

```json
{
  "id": 7,
  "name": "Acme Corp",
  "_links": {
    "self": { "href": "/customers/7" }
  }
}
```

`ActionResult<T>` is supported: when an action returns an `ObjectResult` carrying a value, that value is linked; other result branches (for example `NotFound()`) pass through unmodified.

```csharp
[HttpGet("find/{id:int}")]
[CairnLinks]
public ActionResult<Order> Find(int id)
    => id > 0 ? new Order(id, "Pending") : NotFound();
```

## Route naming

`LinkTarget.Route` resolves against named routes, so controller actions referenced by a `LinkConfig<T>` must be named. Name them with `[HttpGet(Name = "...")]` / `[HttpPost(Name = "...")]` (or `[Route(Name = "...")]`):

```csharp
[ApiController]
[Route("orders")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet("{id:int}", Name = "GetOrderById")]
    [CairnLinks]
    public Order Get(int id) => new(id, "Pending");

    [HttpPost("{id:int}/cancel", Name = "CancelOrder")]
    public IActionResult Cancel(int id) => NoContent();
}
```

```csharp
public sealed class OrderLinks : LinkConfig<Order>
{
    public override void Configure(ILinkBuilder<Order> builder)
    {
        builder.Self(order => LinkTarget.Route("GetOrderById", new { id = order.Id }));
        builder.Affordance("cancel", order => LinkTarget.Route("CancelOrder", new { id = order.Id }))
            .Method("POST")
            .Accepts<CancelRequest>()
            .When(order => order.Status == "Pending");
    }
}
```

The same route names feed route safety: the source generator emits a `Routes.*` catalog from controller route attributes, and the analyzer reports `CAIRN001` when a `LinkTarget.Route` name does not match any named endpoint (and `CAIRN002` on collisions). You can therefore replace the string literals with the generated helpers and get compile-time checking — exactly as for minimal APIs:

```csharp
public override void Configure(ILinkBuilder<Order> builder)
{
    builder.Self(order => Routes.GetOrderById(order.Id));
    builder.Affordance("cancel", order => Routes.CancelOrder(order.Id))
        .Method("POST")
        .Accepts<CancelRequest>()
        .When(order => order.Status == "Pending");
}
```

See [Analyzers & generated routes](route-safety.md).

## Everything else is identical

Because controllers share the engine, configs, and JSON modifier with minimal APIs, no controller-specific features exist:

- Wire formats and content negotiation work the same; set `DefaultFormat` (or negotiate) and `_links` / `_actions` / `_templates` are emitted accordingly. See [Wire formats & negotiation](formats.md).
- Returning `PagedResource<T>` or `CursorPage<T>` from an action produces the paging envelope with its navigation links and links each item. See [Pagination](pagination.md).
- Affordances and HAL-FORMS templates are declared on the config, not the action. See [Affordances & HAL-FORMS](affordances-and-forms.md).
- Embedded resources, link arrays, and CURIEs behave as documented in [Embedded resources](embedded-resources.md).

A returned collection has each element linked individually:

```csharp
[HttpGet]
[CairnLinks]
public IEnumerable<Order> List()
    => [new(1, "Pending"), new(2, "Shipped")];
```

Each item gets its own `self` link, and per-item conditions (such as a `When(...)` affordance) are evaluated against that element's runtime state.
