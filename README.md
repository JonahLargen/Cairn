<h1>Cairn</h1>

<p>Opt-in HATEOAS for .NET — hypermedia you add only where it helps.</p>

---

## What is Cairn?

A cairn is a small stack of stones a traveler leaves along a trail so the next traveler can find the way — placed only where the path is unclear, and followed by choice. Cairn brings that idea to your APIs: **hypermedia links and affordances you add per endpoint, only where they help.**

## Design principles

- **Clean DTOs.** Add links to a plain `record` — no base class, no marker interface, no required attributes. Links are projected at serialization time via a `System.Text.Json` contract modifier, so your models stay untouched.
- **Opt-in by default.** Endpoints you don't opt in to serialize exactly as before, so Cairn is safe to add incrementally to an existing API.
- **Minimal-API-first.** Register with `AddCairn()` and opt endpoints in with `.WithLinks()`.
- **Affordances that authorize.** A `cancel` link can be advertised only when the resource is in a cancellable state **and** the caller satisfies an ASP.NET Core authorization policy — the same policy that guards the action.
- **System.Text.Json-native and AOT-friendly.** No reflection on the hot path.
- **Pragmatic formats.** A flat `{ href, rel, method }` shape and HAL by content negotiation, with HAL-FORMS planned — so links fit whatever your clients already expect.

## Usage

```csharp
// Your DTO stays a plain record — Cairn never touches it.
public record OrderDto(int Id, OrderStatus Status);

// Link/affordance rules live outside the DTO.
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> b)
    {
        b.Self(o => Routes.GetOrderById(o.Id));         // Routes.* generated from named endpoints
        b.Affordance("cancel", o => Routes.CancelOrder(o.Id))
         .Method("POST")
         .When(o => o.Status == OrderStatus.Pending)   // advertise only when applicable
         .RequireAuthorization("CanCancelOrders");      // ASP.NET Core authorization policy
    }
}

builder.Services.AddCairn(o => o.AddLinks(new OrderLinks()));
// ...
app.MapGet("/orders/{id:int}", (int id, IOrderRepo repo) => TypedResults.Ok(repo.Get(id)))
   .WithName("GetOrderById")
   .WithLinks();   // links projected at serialization time; the DTO is never modified
```

Controllers work the same way — opt in with `[CairnLinks]`, using the same `LinkConfig<T>`:

```csharp
[ApiController]
[Route("orders")]
public class OrdersController(IOrderRepo repo) : ControllerBase
{
    [HttpGet("{id:int}", Name = "GetOrderById")]
    [CairnLinks]
    public OrderDto Get(int id) => repo.Get(id);
}
```

## Service-aware links

When a link or affordance depends on something not on the DTO — a service, or a field you didn't project — use the async overloads, which give you the request's services:

```csharp
b.Affordance("cancel", o => Routes.CancelOrder(o.Id))
 .RequireAuthorization("CanCancel")
 .When(async (o, ctx) =>
     await ctx.Services.GetRequiredService<IOrderService>().IsCancelableAsync(o.Id, ctx.CancellationToken));
```

For collections, don't query per item — that's an N+1. Load the facts once in your handler into a scoped holder, then have the condition read it:

```csharp
app.MapGet("/orders", (IOrderService svc, OrderFacts facts) =>
{
    facts.Cancelable = svc.GetCancelable(ids);   // one batch query
    return TypedResults.Ok(orders);
}).WithLinks();

// in the config: .When((o, ctx) => new(ctx.Services.GetRequiredService<OrderFacts>().Cancelable.Contains(o.Id)))
```

## Runtime-type dispatch and diagnostics

Configs dispatch by the value's **runtime type**, falling back to the nearest registered base class — a `LinkConfig<OrderDto>` also covers a returned `RushOrderDto : OrderDto` (interfaces are not considered). Handlers can return the value bare or wrapped (`TypedResults.Ok(...)`, `ObjectResult`); both are linked the same way.

Cairn correlates hypermedia to your instances by reference between computing links and serializing the response, so shapes that break that correlation can't carry links — instead of silently dropping them, Cairn logs a one-time warning per type:

- **Deferred sequences** (`IQueryable`, LINQ projections) returned bare are materialized once and handed to the serializer, so links survive and the underlying query runs once. Inside an immutable result like `TypedResults.Ok(...)` the sequence can't be swapped for its buffer — if its re-enumeration produces new instances the links are lost, and Cairn warns that computed hypermedia was never emitted. Prefer materializing (`ToList()`) before wrapping.
- **`IAsyncEnumerable<T>`** streams cannot be enumerated twice and are not supported — materialize first (e.g. `ToListAsync()`).
- **Value-type resources** (structs) box to a different instance at each stage and cannot carry links — use a class or record.
- **Unregistered types** returned from an endpoint that opted in via `.WithLinks()`/`[CairnLinks]` get a warning naming the missing `LinkConfig<T>`.

## API versioning

Cairn composes with `Asp.Versioning`. Because links resolve through the standard `LinkGenerator`, **URL-segment versioning works automatically** — the current request's version flows into links (a `/v1` request links to `/v1/...`). For **query-string** versioning, carry the version onto links with `TransformUrl`:

```csharp
builder.Services.AddCairn(o => o.TransformUrl = (http, url) =>
    http.Request.Query.TryGetValue("api-version", out var v) && v.Count > 0
        ? QueryHelpers.AddQueryString(url, "api-version", v.ToString())
        : url);
```

Header and media-type versioning keep the version out of the URL by design, so links stay version-neutral and the client re-applies its version.

## Packages

| Package | Purpose |
| --- | --- |
| `Cairn.Core` | Transport-agnostic hypermedia model (links, relations, affordances). No ASP.NET dependency. |
| `Cairn.AspNetCore` | ASP.NET Core integration for both minimal APIs (`.WithLinks()`) and MVC controllers (`[CairnLinks]`). |
| `Cairn.Testing` | Test assertion helpers for links and affordances. |

## Building

```bash
dotnet build Cairn.slnx
dotnet test Cairn.slnx
```

Requires the .NET 10 SDK.

## License

[MIT](LICENSE) © Jonah Largen
