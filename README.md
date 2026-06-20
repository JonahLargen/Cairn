<h1 align="center">Cairn</h1>

<p align="center"><em>Leave a marker. They'll find their way.</em></p>

<p align="center">Opt-in HATEOAS for .NET — hypermedia you add only where it helps.</p>

---

## What is Cairn?

A cairn is a small stack of stones a traveler leaves along a trail so the next traveler can find the way — placed only where the path is unclear, and followed by choice. Cairn brings that idea to your APIs: **hypermedia links and affordances you add per endpoint, only where they help.**

## Design principles

- **Clean DTOs.** Add links to a plain `record` — no base class, no marker interface, no required attributes. Links are projected at serialization time via a `System.Text.Json` contract modifier, so your models stay untouched.
- **Opt-in by default.** Endpoints you don't opt in to serialize exactly as before, so Cairn is safe to add incrementally to an existing API.
- **Minimal-API-first.** `app.MapGroup("/orders").AddCairn()` and `.WithLinks<T>()`, with MVC supported over the same engine.
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
        b.Self(o => Route.GetOrderById(o.Id));
        b.Affordance("cancel", o => Route.CancelOrder(o.Id))
         .Method(HttpMethod.Post)
         .When(o => o.Status == OrderStatus.Pending)   // advertise only when applicable
         .RequireAuthorization("CanCancelOrders");      // ASP.NET Core authorization policy
    }
}

var orders = app.MapGroup("/orders").AddCairn();
orders.MapGet("/{id:int}", (int id, IOrderRepo repo) => TypedResults.Ok(repo.Get(id)))
      .WithLinks<OrderDto>();   // links projected at serialize time; .Produces<OrderDto>() stays intact
```

## Packages

| Package | Purpose |
| --- | --- |
| `Cairn.Core` | Transport-agnostic hypermedia model (links, relations, affordances). No ASP.NET dependency. |
| `Cairn.AspNetCore` | Minimal-API-first ASP.NET Core integration. |
| `Cairn.Testing` | Test assertion helpers for links and affordances. |

## Building

```bash
dotnet build Cairn.slnx
dotnet test Cairn.slnx
```

Requires the .NET 10 SDK.

## License

[MIT](LICENSE) © Jonah Largen
