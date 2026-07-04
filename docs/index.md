# Cairn

**Opt-in HATEOAS for ASP.NET Core** — hypermedia links and actions, added only where they help.

Cairn adds `_links` and actions to your API responses without touching your models. DTOs stay plain `record` types — no base class, no marker interface, no attributes. Hypermedia is declared separately in a `LinkConfig<T>` and injected at serialization time through a `System.Text.Json` contract modifier. Endpoints you don't opt in serialize exactly as before, which makes Cairn safe to adopt one endpoint at a time in an existing API.

Runs on .NET 8, 9, and 10. [Source on GitHub](https://github.com/JonahLargen/Cairn) · MIT licensed.

## The 30-second example

```bash
dotnet add package Cairn.AspNetCore
```

A plain DTO — Cairn never touches it:

```csharp
public record OrderDto(int Id, string Status);
```

The link rules live outside the DTO, in a `LinkConfig<T>`:

```csharp
using Cairn;

public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> b) =>
        b.Self(o => LinkTarget.Route("GetOrderById", new { id = o.Id }));
}
```

Register Cairn and the config, then opt the endpoint in. The route's `.WithName(...)` supplies the name `LinkTarget.Route` resolves against:

```csharp
using Cairn.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCairn(o => o.AddLinks(new OrderLinks()));

var app = builder.Build();

app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new OrderDto(id, "Pending")))
   .WithName("GetOrderById")
   .WithLinks();

app.Run();
```

A request to `/orders/42` now returns the DTO with a `_links` object projected in:

```json
{
  "id": 42,
  "status": "Pending",
  "_links": {
    "self": { "href": "http://localhost:5000/orders/42" }
  }
}
```

That's the whole model. Everything else in these docs — conditional actions, authorization gates, HAL and HAL-FORMS, pagination, forms — is the same `LinkConfig<T>` builder doing more.

## Learn the library

Read these three in order — they build on each other:

1. **[What is HATEOAS?](articles/hateoas.md)** — the concept and vocabulary in five minutes: links, relations, affordances, and why responses that carry them make clients simpler.
2. **[Getting started](articles/getting-started.md)** — a ten-minute walkthrough: install, declare, opt in, and watch a `cancel` action appear and disappear with order state. Includes troubleshooting.
3. **[Link configurations](articles/link-configs.md)** — the full builder: conditions, service-aware and async targets, authorization, titles.

## Guide by topic

**Shaping responses**

- [Wire formats & negotiation](articles/formats.md) — the Default shape, HAL, and HAL-FORMS, selected by `Accept`.
- [Affordances & HAL-FORMS](articles/affordances-and-forms.md) — actions, methods, and derived form fields.
- [Pagination](articles/pagination.md) — offset and cursor paging with `self`/`prev`/`next` links.
- [Embedded resources](articles/embedded-resources.md) — `_embedded`, link arrays, and CURIEs.
- [Custom wire formats](articles/custom-formats.md) — plugging in Siren or a house format.
- [Error responses](articles/error-responses.md) — problem details with links and actions.

**Running in production**

- [API versioning](articles/versioning.md) — composing with `Asp.Versioning`.
- [Link URL policy](articles/url-policy.md) — absolute vs path-relative links, `PublicBaseUri`, the per-request `ResolvePublicBaseUri` for multi-tenant hosts, and `TransformUrl`.
- [Conditional requests, OPTIONS & deprecation](articles/conditional-requests.md) — `WithETag`, preconditions, `Allow`, deprecation headers.
- [Diagnostics & observability](articles/diagnostics.md) — warnings, metrics, and tracing.

**Beyond minimal APIs**

- [Controllers (MVC)](articles/controllers.md) — the same model with `[CairnLinks]`.
- [The typed client](articles/client.md) — `CairnClient`, following links, invoking actions, submitting forms.

**Tooling & quality**

- [Route safety](articles/route-safety.md) — analyzers CAIRN001–003 and the generated `Routes.*` catalog.
- [OpenAPI & Swagger](articles/openapi.md) — documenting hypermedia responses.
- [Testing](articles/testing.md) — asserting on links and affordances; snapshots.

## The package family

| Package | Purpose |
| --- | --- |
| `Cairn.Core` | Transport-agnostic hypermedia model: `Link`, `LinkRelation`, `Affordance`, `LinkTarget`, and the `LinkConfig<T>` builder. No ASP.NET Core dependency. |
| `Cairn.AspNetCore` | ASP.NET Core integration: `AddCairn`, minimal-API `.WithLinks()`, MVC `[CairnLinks]`, pagination, wire formats, problem details. Bundles the route-safety analyzers (`CAIRN001`–`002`), code fixes, and the `Routes.*` source generator (`CAIRN003`). |
| `Cairn.Client` | Typed hypermedia client: `CairnClient`, `Resource<T>`, link following, affordance invocation. |
| `Cairn.Testing` | `HypermediaResponse`, `.Should()` assertions, and `HypermediaSnapshot`. |
| `Cairn.OpenApi` | `AddCairnHypermedia()` for the built-in OpenAPI generator (.NET 10 only). |
| `Cairn.Swashbuckle` | `AddCairnHypermedia()` for Swashbuckle/Swagger. |

Most apps start with just `Cairn.AspNetCore` (it references `Cairn.Core`). Details and framework targets are in [Packages](articles/packages.md).
