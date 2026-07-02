# Getting started

In this guide you'll wire Cairn into a minimal API and watch the responses change. By the end, a `GET /orders/1` will return this — a plain DTO carrying a `self` link and a `cancel` action that appears and disappears with the order's state:

```json
{
  "id": 1,
  "status": "Pending",
  "_links": {
    "self": { "href": "http://localhost:5000/orders/1" },
    "collection": { "href": "http://localhost:5000/orders" }
  },
  "_actions": {
    "cancel": { "href": "http://localhost:5000/orders/1/cancel", "method": "POST" }
  }
}
```

It takes about ten minutes. If you're not sure what `_links` and `_actions` are *for*, read [What is HATEOAS?](hateoas.md) first — it's five minutes and makes everything here more meaningful.

> [!NOTE]
> Cairn changes nothing until you ask it to. Registering it makes link projection *available*; a response only gains hypermedia when its endpoint opts in — `.WithLinks()` for minimal APIs, `[CairnLinks]` for [controllers](controllers.md). Every other endpoint serializes exactly as before, so you can adopt Cairn one endpoint at a time in an existing API.

## Prerequisites

- The .NET 8 SDK or later.
- A web project — either an existing API or a fresh one: `dotnet new web -n OrdersApi`.

Install the ASP.NET Core integration package (it brings `Cairn.Core`, which carries `LinkConfig<T>` and the builder types, with it):

```bash
dotnet add package Cairn.AspNetCore
```

## Step 1 — Start with a plain endpoint

Nothing Cairn-specific yet. A DTO and two endpoints — one that returns an order, one that cancels it:

```csharp
public enum OrderStatus
{
    Pending,
    Shipped,
    Cancelled,
}

public record OrderDto(int Id, OrderStatus Status);
```

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/orders/{id:int}", (int id) =>
    TypedResults.Ok(new OrderDto(id, id % 2 == 0 ? OrderStatus.Shipped : OrderStatus.Pending)));

app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent());

app.Run();
```

(The odd/even status is a stand-in for a real data store, so we can observe both states in a moment.)

Run it and request an order (your port will differ — use the one `dotnet run` prints):

```bash
curl http://localhost:5000/orders/1
```

```json
{ "id": 1, "status": "Pending" }
```

Correct, but mute: a client that wants to cancel this order has to know the cancel URL and the "only while pending" rule on its own. Let's make the response say it.

## Step 2 — Describe the hypermedia in a `LinkConfig<T>`

Cairn never touches the DTO. Instead, its links and actions are declared in a separate class — a `LinkConfig<T>` — which keeps the model clean and puts all hypermedia rules for a type in one place:

```csharp
using Cairn;

public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(order => LinkTarget.Route("GetOrderById", new { id = order.Id }));

        builder.Link("collection", _ => LinkTarget.Route("ListOrders"));

        builder.Affordance("cancel", order => LinkTarget.Route("CancelOrder", new { id = order.Id }))
            .Post()
            .When(order => order.Status == OrderStatus.Pending);
    }
}
```

Line by line:

- **`Self(...)`** declares the resource's canonical URL — the `self` relation every well-behaved resource carries.
- **`LinkTarget.Route("GetOrderById", new { id = order.Id })`** points at a *named endpoint* and lets ASP.NET Core's `LinkGenerator` build the URL. No hand-assembled strings, and the link follows the route if it moves. (`LinkTarget.Uri(...)` exists for explicit URLs, and `LinkTarget.RouteTemplate(...)` for RFC 6570 templates.)
- **`Link("collection", ...)`** adds an ordinary link. The first argument is the **relation** — the name a client looks the link up by.
- **`Affordance("cancel", ...)`** declares an *action*: something a client can do to the order, with a target and an HTTP method (`.Post()`; `Get()`, `Put()`, `Patch()`, `Delete()`, and `Method(...)` also exist).
- **`.When(...)`** is the state rule: the `cancel` action is only advertised while the order is `Pending`. This one line replaces the same rule re-implemented in every client.

The builder can do much more — service-aware and async targets, authorization-gated links, titles, embedding — but this is the shape of all of it. See [Link configurations](link-configs.md).

## Step 3 — Register Cairn and opt the endpoints in

Three changes to `Program.cs`, marked below: register the config with `AddCairn`, **name** the routes (that's what `LinkTarget.Route` resolves against), and add `.WithLinks()` to the endpoints whose responses should carry hypermedia:

```csharp
using Cairn.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCairn(options => options.AddLinks(new OrderLinks()));   // 1. register

var app = builder.Build();

app.MapGet("/orders/{id:int}", (int id) =>
        TypedResults.Ok(new OrderDto(id, id % 2 == 0 ? OrderStatus.Shipped : OrderStatus.Pending)))
    .WithName("GetOrderById")    // 2. name the route...
    .WithLinks();                // 3. ...and opt in

app.MapGet("/orders", () => TypedResults.Ok(new[]
    {
        new OrderDto(1, OrderStatus.Pending),
        new OrderDto(2, OrderStatus.Shipped),
    }))
    .WithName("ListOrders")
    .WithLinks();

// The cancel target needs a name (links point at it) but no .WithLinks() — it returns no body.
app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent())
    .WithName("CancelOrder");

app.Run();
```

Two details worth noticing:

- The route **name** (`WithName("GetOrderById")`) is the contract between the endpoint and the config. If they drift apart, the link can't resolve — [Strict mode](#strict-mode-catch-broken-links-early) and the [CAIRN001 analyzer](route-safety.md) both exist to catch exactly that.
- Registering configs one by one is fine to start; real apps usually use `options.AddLinksFromAssemblyContaining<Program>()` to pick up every `LinkConfig<T>` in the assembly.

## Step 4 — See the state machine in the response

Request a **pending** order:

```bash
curl http://localhost:5000/orders/1
```

```json
{
  "id": 1,
  "status": "Pending",
  "_links": {
    "self": { "href": "http://localhost:5000/orders/1" },
    "collection": { "href": "http://localhost:5000/orders" }
  },
  "_actions": {
    "cancel": { "href": "http://localhost:5000/orders/1/cancel", "method": "POST" }
  }
}
```

Now a **shipped** one:

```bash
curl http://localhost:5000/orders/2
```

```json
{
  "id": 2,
  "status": "Shipped",
  "_links": {
    "self": { "href": "http://localhost:5000/orders/2" },
    "collection": { "href": "http://localhost:5000/orders" }
  }
}
```

Same endpoint, same config — but the `cancel` action is gone, because `.When(...)` said so. A client decides whether to show a Cancel button by checking for `_actions.cancel`, and the business rule lives in exactly one place: the server.

The collection endpoint works too — each element is linked according to its runtime type, so `GET /orders` returns an array where every order carries its own `_links` (and `cancel` only on the pending one). Endpoints returning `Results<Ok<T>, NotFound>` unions are also unwrapped and linked when a value is present.

> [!TIP]
> Links are absolute URLs built from the incoming request by default (hence `http://localhost:5000/...` above). Behind a proxy, or if you prefer path-relative links like `"/orders/1"`, see [Link URL policy](url-policy.md).

This response shape is Cairn's **Default** format. The same declaration can also be served as HAL or HAL-FORMS, selected by the request's `Accept` header — try `curl -H "Accept: application/hal+json" http://localhost:5000/orders/1` and watch `_actions` change shape. See [Wire formats & negotiation](formats.md).

## How it works

There's no magic middleware rewriting your JSON. For an opted-in endpoint:

1. Your handler runs and returns the DTO as usual.
2. Cairn looks up the value's **runtime type** in the registered configs (a collection is looked up per element) and computes its links and actions — resolving route names through the standard `LinkGenerator`, evaluating each `When(...)` and authorization gate.
3. At serialization time, a `System.Text.Json` contract modifier projects `_links`/`_actions` into the type's JSON — the DTO instance itself is never modified.

Endpoints without `.WithLinks()` never reach step 2, which is why the rest of your API is untouched.

## Strict mode: catch broken links early

What if a config names a route that doesn't exist — a typo, or an endpoint that was renamed? `CairnOptions.Mode` decides:

- **`LinkResolutionMode.Lax`** (the default) omits the unresolvable link. Production keeps serving; the drop is logged once per type/relation and counted on the `cairn.links.unresolved` metric ([diagnostics](diagnostics.md)).
- **`LinkResolutionMode.Strict`** throws a `LinkResolutionException` instead — the response either has the link or fails loudly.

Use Strict in development and tests so broken targets surface immediately:

```csharp
builder.Services.AddCairn(options =>
{
    options.Mode = builder.Environment.IsDevelopment()
        ? LinkResolutionMode.Strict
        : LinkResolutionMode.Lax;
    options.AddLinks(new OrderLinks());
});
```

To rule out route-name typos at *compile* time instead, the analyzers and source generator bundled in `Cairn.AspNetCore` check names and generate a typed `Routes.*` catalog (`Routes.GetOrderById(order.Id)`) — nothing extra to install. See [Route safety](route-safety.md).

## Troubleshooting

The failure modes below are by far the most common first-run issues. Cairn logs a one-time warning for each of them — check your application log before anything else.

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| No `_links` at all | The endpoint never opted in | Add `.WithLinks()` (or `[CairnLinks]` on the action) |
| No `_links`, endpoint is opted in | No `LinkConfig<T>` registered for the returned type — configs dispatch on the value's **runtime type** (base classes count, interfaces don't) | Register the config with `AddLinks(...)` / `AddLinksFromAssemblyContaining<T>()`; the [CAIRN002 analyzer](route-safety.md) catches this at build time |
| One specific link/action missing | Route-name typo (Lax mode drops it), a `When(...)` that's false, or an unmet `RequireAuthorization(...)` policy | Switch to Strict mode in dev; check the predicate and policy against your test request |
| Links missing on a wrapped `IQueryable`/LINQ projection | Re-enumeration inside `TypedResults.Ok(...)` produced new instances, so computed links couldn't be matched back up | Materialize first: `.ToList()` before wrapping ([diagnostics](diagnostics.md)) |
| `IAsyncEnumerable<T>` response has no links | Streams can't be enumerated twice; not supported | Materialize first (e.g. `ToListAsync()`) |
| A struct resource has no links | Value types box to different instances between compute and serialize | Use a class or record |

## Try the sample

A complete runnable example lives in [`samples/Cairn.Sample.Api`](https://github.com/JonahLargen/Cairn/tree/main/samples/Cairn.Sample.Api): minimal-API endpoints and an MVC controller, collections, offset and cursor pagination, and `Results<,>` unions — all over plain record DTOs.

## Next steps

**Shape richer responses**

- [Link configurations](link-configs.md) — conditions, service-aware targets, authorization, titles.
- [Affordances & HAL-FORMS](affordances-and-forms.md) — actions with typed input forms.
- [Wire formats & negotiation](formats.md) — Default, HAL, and HAL-FORMS.
- [Pagination](pagination.md) — `self`/`prev`/`next` links on paged envelopes.
- [Embedded resources](embedded-resources.md) — inline related resources with their own links.

**Round out the API**

- [Controllers (MVC)](controllers.md) — the same model with `[CairnLinks]`.
- [OpenAPI & Swagger](openapi.md) — document the hypermedia.
- [Testing](testing.md) — assert on links and actions in integration tests.
- [The typed client](client.md) — consume the links you just emitted.
