# Getting started

This guide wires Cairn into a minimal API for the first time: install the package, define a plain DTO, declare its hypermedia in a `LinkConfig<T>`, register it with `AddCairn`, opt an endpoint in with `.WithLinks()`, and read the linked response. The same model drives controllers (see [controllers.md](controllers.md)).

## The opt-in model

Cairn changes nothing until you ask it to. Registering services with `AddCairn` makes the link projection *available*, but a response only gains links when its endpoint opts in:

- Minimal APIs opt in per endpoint or route group with `.WithLinks()`.
- MVC actions opt in with the `[CairnLinks]` attribute (see [controllers.md](controllers.md)).

An endpoint without `.WithLinks()` or `[CairnLinks]` serializes exactly as it would have. There is no global filter, no convention scanning your responses, and no change to DTOs — Cairn attaches links to a value's serialized form based on its runtime type's registered configuration.

## Install

Add the ASP.NET Core integration package. It depends on `Cairn.Core`, which carries `LinkConfig<T>` and the builder types:

```bash
dotnet add package Cairn.AspNetCore
```

See [packages.md](packages.md) for the full package list.

## 1. Define a DTO

The DTO stays a plain record. Cairn requires no base type, interface, or attribute on it:

```csharp
public enum OrderStatus
{
    Pending,
    Shipped,
    Cancelled,
}

public record OrderDto(int Id, OrderStatus Status);
```

## 2. Declare the links

Derive a `LinkConfig<T>` for the DTO and override `Configure(ILinkBuilder<T> builder)`. Here the order gets a `self` link, a related link, and a state-conditional `cancel` affordance:

```csharp
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(order => LinkTarget.Route("GetOrderById", new { id = order.Id }));

        builder.Link("collection", _ => LinkTarget.Route("ListOrders"));

        builder.Affordance("cancel", order => LinkTarget.Route("CancelOrder", new { id = order.Id }))
            .Method("POST")
            .When(order => order.Status == OrderStatus.Pending);
    }
}
```

- `Self`, `Link`, and `Affordance` take a delegate from the resource to a `LinkTarget`. `LinkTarget.Route(routeName, routeValues?)` points at a named endpoint and is resolved to a URL by the host; `LinkTarget.Uri(href, templated?)` points at an explicit URI or URI template; `LinkTarget.RouteTemplate(routeName, routeValues?)` renders a named route as an RFC 6570 URI template, leaving unbound route parameters as `{placeholders}`.
- A `LinkRelation` (the `rel`, here `"collection"` and `"cancel"`) is created implicitly from a string.
- `.When(...)` includes a link or affordance only when the predicate holds — `cancel` is omitted unless the order is `Pending`.
- `.Method("POST")` sets the affordance's HTTP method (the shorthands `Get()`, `Post()`, `Put()`, `Patch()`, and `Delete()` exist too).

The builder also exposes link arrays, service-aware and async targets, embedding, titles, and authorization — see [link-configs.md](link-configs.md) and [affordances-and-forms.md](affordances-and-forms.md).

## 3. Register with AddCairn

Call `AddCairn` and register each configuration with `AddLinks`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCairn(options =>
{
    options.AddLinks(new OrderLinks());
});

var app = builder.Build();
```

To register every `LinkConfig<T>` in an assembly instead of one at a time, use `options.AddLinksFromAssembly(assembly)` or `options.AddLinksFromAssemblyContaining<T>()`.

## 4. Name the endpoint and opt in

`LinkTarget.Route` resolves against named routes, so give the target endpoint a name with `.WithName(...)`, and add `.WithLinks()` to each endpoint whose response should carry links:

```csharp
var orders = app.MapGroup("/orders");

orders.MapGet("/{id:int}", (int id) =>
        TypedResults.Ok(new OrderDto(id, OrderStatus.Pending)))
    .WithName("GetOrderById")
    .WithLinks();

orders.MapGet("/", () => TypedResults.Ok(new[]
    {
        new OrderDto(1, OrderStatus.Pending),
        new OrderDto(2, OrderStatus.Shipped),
    }))
    .WithName("ListOrders")
    .WithLinks();

// The target the 'cancel' affordance points at — no .WithLinks() needed; it returns no body.
orders.MapPost("/{id:int}/cancel", (int id) => TypedResults.NoContent())
    .WithName("CancelOrder");

app.Run();
```

`.WithLinks()` applies to a single endpoint or to a whole route group (any `IEndpointConventionBuilder`). Each returned value — and each element of a returned collection — is linked according to its runtime type's configuration, so the collection endpoint above links every `OrderDto` it returns. It also unwraps `Results<,>` unions, linking the inner value when one is present.

## 5. Read the response

`GET /orders/1` now returns the DTO with a `_links` object and, because the order is `Pending`, a `cancel` affordance:

```json
{
  "id": 1,
  "status": "Pending",
  "_links": {
    "self": { "href": "/orders/1" },
    "collection": { "href": "/orders" }
  },
  "_actions": {
    "cancel": { "href": "/orders/1/cancel", "method": "POST" }
  }
}
```

This is the Default wire format. Cairn can also emit HAL and HAL-FORMS, selected by content negotiation or forced per endpoint — see [formats.md](formats.md).

## Resolution mode: Lax vs Strict

`CairnOptions.Mode` controls what happens when a `LinkTarget` cannot be resolved to a URL — for example, a route name that matches no endpoint:

- `LinkResolutionMode.Lax` (the default) omits links that fail to resolve. The drop is not silent: each one increments the `cairn.links.unresolved` counter and is logged once per resource type and relation — see [diagnostics.md](diagnostics.md).
- `LinkResolutionMode.Strict` throws a `LinkResolutionException` instead.

Use `Strict` in development and tests to catch broken targets early; the response either has the link or fails loudly:

```csharp
builder.Services.AddCairn(options =>
{
    options.Mode = LinkResolutionMode.Strict;
    options.AddLinks(new OrderLinks());
});
```

To eliminate magic route-name strings entirely, the analyzer and source-generator packages provide a compile-checked `Routes.*` catalog (e.g. `Routes.GetOrderById(order.Id)`) and flag unknown route names — see [route-safety.md](route-safety.md).

## The sample app

A complete, runnable example lives in `samples/Cairn.Sample.Api`. It registers `OrderLinks` and `CustomerLinks`, opts minimal-API endpoints in with `.WithLinks()`, opts an MVC controller in with `[CairnLinks]`, and demonstrates collections, offset and cursor pagination ([pagination.md](pagination.md)), and `Results<,>` unions — all over plain record DTOs.

## Next steps

- [link-configs.md](link-configs.md) — the builder, conditions, service-aware targets, and authorization.
- [affordances-and-forms.md](affordances-and-forms.md) — affordances and HAL-FORMS fields.
- [formats.md](formats.md) — Default, HAL, and HAL-FORMS wire formats and negotiation.
- [controllers.md](controllers.md) — opting MVC actions in with `[CairnLinks]`.
- [diagnostics.md](diagnostics.md) — logged warnings, metrics, and tracing for hypermedia failures.
- [client.md](client.md) — consuming linked responses with the typed client.
