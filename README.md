<div align="center">

# Cairn

**Opt-in HATEOAS for ASP.NET Core — hypermedia links and actions, added only where they help.**

[![NuGet](https://img.shields.io/nuget/v/Cairn.AspNetCore.svg?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Cairn.AspNetCore)
[![Coverage](https://img.shields.io/codecov/c/github/JonahLargen/Cairn?logo=codecov&label=Coverage)](https://app.codecov.io/gh/JonahLargen/Cairn)
[![CI](https://github.com/JonahLargen/Cairn/actions/workflows/ci.yml/badge.svg)](https://github.com/JonahLargen/Cairn/actions/workflows/ci.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/JonahLargen/Cairn/badge)](https://scorecard.dev/viewer/?uri=github.com/JonahLargen/Cairn)
[![License: MIT](https://img.shields.io/github/license/JonahLargen/Cairn?label=license&color=green)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)

[Documentation](https://jonahlargen.github.io/Cairn) ·
[Getting started](https://jonahlargen.github.io/Cairn/articles/getting-started.html) ·
[Sample API](samples/Cairn.Sample.Api) ·
[Releases](https://github.com/JonahLargen/Cairn/releases)

</div>

---

Cairn adds hypermedia to ASP.NET Core APIs without touching your models. DTOs stay plain `record` types, link rules live in a separate `LinkConfig<T>`, and endpoints opt in one at a time — everything you don't opt in serializes exactly as before, so Cairn is safe to introduce incrementally into an existing API.

## What is HATEOAS?

*Hypermedia As The Engine of Application State* is the idea that an API response should tell the client **where it can go** and **what it can do next**, instead of leaving the client to hardcode URLs and business rules.

A typical API returns bare data:

```json
{ "id": 42, "status": "Pending" }
```

To act on this, the client has to already know things that aren't in the response: how to build the order's URL, that orders can only be cancelled while pending, and whether *this* user is allowed to cancel. That knowledge gets duplicated into every client — and silently breaks when the server changes.

A hypermedia response carries the knowledge with the data:

```json
{
  "id": 42,
  "status": "Pending",
  "_links": {
    "self": { "href": "https://api.example.com/orders/42" }
  },
  "_actions": {
    "cancel": { "href": "https://api.example.com/orders/42/cancel", "method": "POST" }
  }
}
```

Reading it top to bottom:

- **`_links`** answers *"where can I go from here?"* Each key is a **relation** — `self` is the canonical URL of this resource; a collection page adds relations like `next` and `prev`.
- **`_actions`** answers *"what can I do right now?"* Each entry is an **affordance**: a state transition with a target URL and HTTP method.
- **`cancel` is present because it's currently valid** — the order is `Pending`, and the caller passed the authorization policy that guards cancellation.

Fetch the same order after it ships, and the response changes:

```json
{
  "id": 42,
  "status": "Shipped",
  "_links": {
    "self": { "href": "https://api.example.com/orders/42" }
  }
}
```

The `cancel` action is gone. That is the "engine of application state" part: the server — the only party that actually knows the rules — tells the client what is possible, and the client's job reduces to *"render a Cancel button if `_actions.cancel` exists."* No duplicated state machine, no duplicated permission checks, no hardcoded URLs.

New to the concept? The docs have a longer, gentler introduction: [What is HATEOAS?](https://jonahlargen.github.io/Cairn/articles/hateoas.html)

## Why Cairn?

Most hypermedia libraries want to own your whole API: base classes on your DTOs, wrapper types on your responses, a global formatter over every endpoint. Cairn deliberately does the opposite.

- **Clean DTOs.** Links are declared *outside* the model and injected at serialization time through a `System.Text.Json` contract modifier — no base class, no marker interface, no attributes on your types.
- **Opt-in per endpoint.** `.WithLinks()` on a minimal-API endpoint or `[CairnLinks]` on a controller action. Everything else is byte-for-byte unchanged.
- **Affordances that authorize.** An action can be advertised only when the resource is in the right state *and* the caller satisfies an ASP.NET Core authorization policy — the same policy that guards the endpoint itself.
- **One config, three formats.** Declare links once; serve Cairn's flat default shape, HAL, or HAL-FORMS via `Accept`-header negotiation, or plug in your own format.
- **Tooling that keeps it honest.** Roslyn analyzers catch broken route names and unconfigured types at compile time, a source generator gives you a typed `Routes.*` catalog instead of magic strings, and dedicated packages cover OpenAPI/Swagger docs, a browsable HAL explorer, test assertions, and a typed client.

## Quick start

Install the ASP.NET Core package:

```bash
dotnet add package Cairn.AspNetCore
```

**1. Your DTO stays a plain record** — Cairn never modifies it:

```csharp
public enum OrderStatus { Pending, Shipped, Cancelled }

public record OrderDto(int Id, OrderStatus Status);
```

**2. Declare the hypermedia in a `LinkConfig<T>`**, separate from the model:

```csharp
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(order => LinkTarget.Route("GetOrderById", new { id = order.Id }));

        builder.Affordance("cancel", order => LinkTarget.Route("CancelOrder", new { id = order.Id }))
            .Post()
            .When(order => order.Status == OrderStatus.Pending);
    }
}
```

**3. Register Cairn and opt the endpoint in.** `LinkTarget.Route` resolves against endpoint names, so name the routes with `.WithName(...)`:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCairn(options => options.AddLinks(new OrderLinks()));

var app = builder.Build();

app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new OrderDto(id, OrderStatus.Pending)))
   .WithName("GetOrderById")
   .WithLinks();                       // ← this endpoint's responses now carry hypermedia

app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent())
   .WithName("CancelOrder");

app.Run();
```

**4. Call it.** `GET /orders/42` now returns the DTO with its links and — because the order is pending — the `cancel` action:

```json
{
  "id": 42,
  "status": "Pending",
  "_links": {
    "self": { "href": "https://localhost:7043/orders/42" }
  },
  "_actions": {
    "cancel": { "href": "https://localhost:7043/orders/42/cancel", "method": "POST" }
  }
}
```

Controllers work the same way with the same `LinkConfig<T>` — opt an action (or the whole controller) in with `[CairnLinks]`:

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

The step-by-step walkthrough — including what to do when links *don't* appear — is in [Getting started](https://jonahlargen.github.io/Cairn/articles/getting-started.html).

## A tour of the toolbox

### Actions gated by state *and* permissions

`When(...)` gates an affordance on resource state; `RequireAuthorization(...)` gates it on the caller — evaluated against real ASP.NET Core policies, memoized per request. The response advertises exactly the actions this caller can take on this resource, right now:

```csharp
builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id }))
    .Post()
    .When(o => o.Status == OrderStatus.Pending)
    .RequireAuthorization("CanCancelOrders");   // same policy that guards the endpoint
```

Need a per-item decision — "may this caller cancel *this* order?" — rather than a caller-wide one? The resource-based overload `RequireAuthorization("CancelOrder", o => o)` hands the resource to your ASP.NET Core policy handlers as `context.Resource`. Conditions can also be service-aware and async when the decision needs more than the DTO — see [Link configurations](https://jonahlargen.github.io/Cairn/articles/link-configs.html).

### One declaration, three wire formats

The same `LinkConfig<T>` serves three built-in shapes, selected by the request's `Accept` header (or forced per endpoint):

| `Accept` | Format | Affordances emitted as |
| --- | --- | --- |
| `application/json` | Default (flat) | `_actions` |
| `application/hal+json` | [HAL](https://datatracker.ietf.org/doc/html/draft-kelly-json-hal) | — (HAL has no actions) |
| `application/prs.hal-forms+json` | [HAL-FORMS](https://rwcbook.github.io/hal-forms/) | `_templates` |

HAL-FORMS templates go further than a URL and a method: `Accepts<TInput>()` derives full form field descriptions (types, required flags, ranges, enum options) from your input type's data annotations, so a client can *render the form* without knowing the type. Custom formats such as Siren plug in through `IHypermediaFormatter` and participate in the same negotiation. See [Wire formats](https://jonahlargen.github.io/Cairn/articles/formats.html) and [Affordances & HAL-FORMS](https://jonahlargen.github.io/Cairn/articles/affordances-and-forms.html).

Opt-in cuts a second way, too: set `DefaultFormat = HypermediaFormat.None` and hypermedia becomes opt-in *by the client*. A plain `application/json` request then returns the bare resource, and links appear only when the caller's `Accept` header asks for a hypermedia media type — so callers that just want data aren't paying for links they'll ignore.

### Pagination links for free

Return a `PagedResource<T>` (offset) or `CursorPage<T>` (keyset) and the envelope gets `self`/`first`/`prev`/`next`/`last` links derived from the request URL — while each item on the page still gets its own links:

```json
"_links": {
  "self":  { "href": "https://api.example.com/orders?page=2" },
  "prev":  { "href": "https://api.example.com/orders?page=1" },
  "next":  { "href": "https://api.example.com/orders?page=3" }
}
```

Existing envelope types can join in without being changed, via `AddPaging<T>`. See [Pagination](https://jonahlargen.github.io/Cairn/articles/pagination.html).

### A typed client that walks the links

`Cairn.Client` consumes what the server emits — follow relations, check for affordances, and invoke them, without building a URL anywhere:

```csharp
var order = (await client.GetAsync<Order>("/orders/42")).Resource!;

if (order.HasAffordance("cancel"))
{
    await order.InvokeAsync("cancel");
}
```

See [The typed client](https://jonahlargen.github.io/Cairn/articles/client.html).

### Test assertions for your hypermedia contract

`Cairn.Testing` parses a response and asserts on links, actions, and forms — framework-agnostic, with URL patterns that survive the random port of an in-memory test server:

```csharp
var hypermedia = await client.GetHypermediaAsync("/orders/42");

hypermedia.Should()
    .HaveSelfLink()
    .And.HaveAffordance("cancel").WithMethod(HttpMethod.Post)
    .And.NotHaveAffordance("delete");
```

`HypermediaSnapshot` renders stable, snapshot-friendly JSON for approval-style tests. See [Testing](https://jonahlargen.github.io/Cairn/articles/testing.html).

### Compile-time route safety

Link targets reference routes by name, and magic strings rot. `Cairn.AspNetCore` bundles Roslyn analyzers and a source generator — nothing extra to install — that close the loop:

- **CAIRN001** flags a `LinkTarget.Route("name")` that no endpoint declares (with a code fix).
- **CAIRN002** flags a `.WithLinks()` endpoint returning a type that has no `LinkConfig<T>` — the classic silent no-op.
- The source generator builds a typed `Routes.*` catalog from your named endpoints, so configs can say `Routes.GetOrderById(order.Id)` and get compile errors instead of broken links.

See [Route safety](https://jonahlargen.github.io/Cairn/articles/route-safety.html).

### Browse your API

`Cairn.AspNetCore.Explorer` serves a **HAL Explorer** — an in-browser console that navigates your live API the way a hypermedia client does: follow `_links`, drill into `_embedded`, and run HAL-FORMS actions as real forms.

```csharp
app.UseCairnExplorer();   // browse at /explorer — Development only by default
```

The UI is a single embedded HTML document (no CDN, no build step) and fetches on the same origin with the caller's credentials, so it shows exactly the links and actions the current user is authorized to see. See [Browsable API explorer](https://jonahlargen.github.io/Cairn/articles/explorer.html).

### Also in the box

- **[Embedded resources](https://jonahlargen.github.io/Cairn/articles/embedded-resources.html)** — HAL `_embedded`, link arrays, and CURIEs.
- **[API versioning](https://jonahlargen.github.io/Cairn/articles/versioning.html)** — composes with `Asp.Versioning`; URL-segment versions flow into links automatically.
- **[Link URL policy](https://jonahlargen.github.io/Cairn/articles/url-policy.html)** — absolute URLs by default, with `PublicBaseUri` pinning (or a per-request `ResolvePublicBaseUri` for multi-tenant hosts) and path-relative mode for proxied deployments.
- **[Conditional requests](https://jonahlargen.github.io/Cairn/articles/conditional-requests.html)** — `WithETag(...)`, precondition evaluation (304/412/428), an OPTIONS handler, and deprecation headers.
- **[Error responses](https://jonahlargen.github.io/Cairn/articles/error-responses.html)** — problem details (RFC 9457) that carry links and actions.
- **[OpenAPI & Swagger](https://jonahlargen.github.io/Cairn/articles/openapi.html)** — hypermedia properties documented in your OpenAPI schema.
- **[Diagnostics & observability](https://jonahlargen.github.io/Cairn/articles/diagnostics.html)** — an `ActivitySource`, a `Meter`, and one-time warnings for every silent-failure mode.

## When is hypermedia worth it?

Cairn is opt-in because hypermedia isn't free and isn't always the right call. It pays off when:

- **Resources are state machines.** Orders, approvals, subscriptions, tickets — anything where "what you can do" depends on "what state it's in". The server owns the transition rules once, instead of every client reimplementing them.
- **UIs are permission-aware.** Rendering buttons from `_actions` means the frontend never re-implements authorization logic — and never shows an action the API would reject.
- **Clients navigate rather than construct.** Pagination, search results, and workflow chains where following `next` beats string-building URLs.
- **URLs need freedom to change.** Clients that follow links survive route restructuring; clients that build URLs from templates don't.

If your API is internal, its clients are generated from an OpenAPI spec, and its resources have no interesting state — plain JSON is fine, and Cairn will happily stay out of those endpoints. Opt in the ones where it helps.

One structural caveat: **streaming responses (`IAsyncEnumerable<T>`) don't get links.** Cairn computes hypermedia before serialization and an async stream can't be enumerated twice, so streamed items serialize without `_links` (a one-time warning is logged). Materialize first (e.g. `ToListAsync()`) — or leave streaming endpoints out of Cairn.

## Packages

| Package | Purpose | Frameworks |
| --- | --- | --- |
| [`Cairn.Core`](https://www.nuget.org/packages/Cairn.Core) | Transport-agnostic hypermedia model (links, relations, affordances). No ASP.NET dependency. | net8.0 · net9.0 · net10.0 |
| [`Cairn.AspNetCore`](https://www.nuget.org/packages/Cairn.AspNetCore) | ASP.NET Core integration: minimal APIs (`.WithLinks()`) and MVC (`[CairnLinks]`), formats, pagination. Bundles the analyzers, code fixes, and `Routes.*` source generator. | net8.0 · net9.0 · net10.0 |
| [`Cairn.Client`](https://www.nuget.org/packages/Cairn.Client) | Typed client for consuming hypermedia APIs. | net8.0 · net9.0 · net10.0 |
| [`Cairn.Testing`](https://www.nuget.org/packages/Cairn.Testing) | Assertions and snapshots for links and affordances. | net8.0 · net9.0 · net10.0 |
| [`Cairn.Swashbuckle`](https://www.nuget.org/packages/Cairn.Swashbuckle) | Hypermedia in Swashbuckle Swagger documents. | net8.0 · net9.0 · net10.0 |
| [`Cairn.OpenApi`](https://www.nuget.org/packages/Cairn.OpenApi) | Hypermedia in `Microsoft.AspNetCore.OpenApi` documents. | net10.0 |
| [`Cairn.AspNetCore.Explorer`](https://www.nuget.org/packages/Cairn.AspNetCore.Explorer) | A browsable HAL Explorer UI (`UseCairnExplorer()`), served from the app. Development-only by default. | net8.0 · net9.0 · net10.0 |

`Cairn.OpenApi` builds on the schema-transformer pipeline that only exists in this shape on .NET 10; on .NET 8/9, use `Cairn.Swashbuckle` instead. Details in [Packages](https://jonahlargen.github.io/Cairn/articles/packages.html).

## Performance

Hypermedia is computed per response, so the overhead is measurable — and measured. The [benchmarks](benchmarks/Cairn.Benchmarks) serve the same page (50 items as a representative size, 1,000 as a stress case) end to end five ways — no links, links hand-rolled in the handler, `WithLinks` with route-based and with explicit-URI configs, and `WithLinks` over unconfigured items — plus single-resource and serializer-only suites. The [benchmark README](benchmarks/Cairn.Benchmarks/README.md) explains how to read the results (marginal per-item cost, not ratios against a near-empty baseline) and which numbers are deliberate non-goals:

```bash
dotnet run -c Release --project benchmarks/Cairn.Benchmarks
```

## Building from source

```bash
dotnet build Cairn.slnx
dotnet test Cairn.slnx
```

Building requires the .NET 10 SDK; running the full multi-targeted test suite additionally needs the .NET 8 and .NET 9 runtimes. The shipped packages run on .NET 8 (LTS) and later. A complete runnable example lives in [`samples/Cairn.Sample.Api`](samples/Cairn.Sample.Api).

## Why "Cairn"?

A cairn is a small stack of stones a traveler leaves along a trail so the next traveler can find the way — placed only where the path is unclear, and followed by choice. That's the design philosophy: hypermedia added deliberately, where it guides, never imposed everywhere.

## License

[MIT](LICENSE) © Jonah Largen
