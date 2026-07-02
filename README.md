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
- **System.Text.Json-native.** Links are injected through contract customization, and Cairn's own wire types (`_links`/`_actions`/`_templates` payloads) ship with a source-generated `JsonSerializerContext`, so hypermedia serializes even when the app uses a source-gen-only `TypeInfoResolver`. Registration and HAL-FORMS schema derivation use reflection (once per type, cached), so full Native AOT publishing is not yet a supported claim.
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

## Caching and authorization-gated links

Two properties of hypermedia responses matter when a cache sits in front of your API:

- **Responses vary by `Accept`.** With format negotiation enabled (the default), the same URL can return three different body shapes and media types. Cairn adds `Vary: Accept` to negotiable responses so shared caches key on it — leave that intact if you post-process headers.
- **Authorization-gated links personalize the body.** A link guarded by `RequireAuthorization(...)` (or a caller-dependent `When`) makes the response body *per-caller*: output-caching such an endpoint (ASP.NET Core `OutputCache`, a CDN) will replay the **first caller's affordance set to every other caller** — leaking what actions that caller could take, and advertising actions the current caller can't. Don't output-cache endpoints whose links are caller-dependent, or vary the cache by the credential.

`RequireAuthorization("Policy")` evaluates the policy against the **caller**, once per request (results are memoized, so a 200-item page evaluates each policy once, not 200 times). It is *not* a per-resource decision: a policy named `CanCancelThisOrder` still can't see the order. For per-resource decisions, combine a caller check with a resource predicate, or call `IAuthorizationService.AuthorizeAsync(user, resource, policy)` yourself in a service-aware `When`.

## Embedding and the response body

`Embed`/`EmbedMany` place a child resource in HAL `_embedded`, decorated with its own links. The child is typically also a normal property of your DTO, and Cairn never removes properties — so it will appear **twice** (in the body and in `_embedded`) unless you mark the property `[JsonIgnore]` or project a DTO without it. That attribute is the one exception to "Cairn never touches your DTO" being enough on its own.

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
