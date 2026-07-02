# Cairn

Opt-in HATEOAS for .NET (8, 9, and 10). Your DTOs stay plain `record` types — no base class, no marker interface, no attributes. Links and affordances are declared separately in a `LinkConfig<T>` and injected at serialization time through a `System.Text.Json` contract modifier, so your models are never modified. Endpoints you don't opt in serialize exactly as before, which makes Cairn safe to add incrementally to an existing API.

## The package family

| Package | Purpose |
| --- | --- |
| `Cairn.Core` | Transport-agnostic hypermedia model: `Link`, `LinkRelation`, `Affordance`, `LinkTarget`, and the `LinkConfig<T>` builder. No ASP.NET Core dependency. |
| `Cairn.AspNetCore` | ASP.NET Core integration: `AddCairn`, minimal-API `.WithLinks()`, MVC `[CairnLinks]`, pagination, wire formats (Default/HAL/HAL-FORMS), and problem details. |
| `Cairn.Client` | Typed hypermedia client: `CairnClient`, `Resource<T>`, link following, and affordance invocation. |
| `Cairn.OpenApi` | `AddCairnHypermedia()` for the built-in OpenAPI document generator (.NET 10 only). |
| `Cairn.Swashbuckle` | `AddCairnHypermedia()` for Swashbuckle/Swagger. |
| `Cairn.Analyzers` / `Cairn.CodeFixes` | Diagnostics for unknown route names (`CAIRN001`, with a code fix) and `WithLinks` endpoints whose return type has no `LinkConfig` (`CAIRN002`). |
| `Cairn.SourceGenerators` | Generates a `Routes.*` catalog from named endpoints and controller route attributes; reports colliding names (`CAIRN003`). |
| `Cairn.Testing` | `HypermediaResponse`, `.Should()` assertions, and `HypermediaSnapshot` for links and affordances. |

## Install

```bash
dotnet add package Cairn.AspNetCore
```

`Cairn.AspNetCore` references `Cairn.Core`, so installing it brings the hypermedia model with it.

## 30-second example

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

A request to `/orders/42` returns the DTO with a `_links` object projected in:

```json
{
  "id": 42,
  "status": "Pending",
  "_links": {
    "self": { "href": "/orders/42" }
  }
}
```

## Where to next

- [Getting started](articles/getting-started.md) — install, register, and opt in your first endpoint.
- [Link configurations](articles/link-configs.md) — the builder, conditions, service-aware targets, and authorization.
- [Wire formats & negotiation](articles/formats.md) — the Default shape, HAL, and HAL-FORMS.
- [Pagination](articles/pagination.md) — offset and cursor paging.
- [Embedded resources](articles/embedded-resources.md) — `_embedded`, link arrays, and CURIEs.
- [Affordances & HAL-FORMS](articles/affordances-and-forms.md) — actions, methods, and form fields.
- [Custom wire formats](articles/custom-formats.md) — plugging in Siren or a house format with `IHypermediaFormatter`.
- [Controllers / MVC](articles/controllers.md) — opting in with `[CairnLinks]`.
- [Error responses](articles/error-responses.md) — problem details with links and actions.
- [API versioning](articles/versioning.md) — composing with versioned routes.
- [Link URL policy](articles/url-policy.md) — absolute vs path-relative links, `PublicBaseUri`, `TransformUrl`.
- [Conditional requests, OPTIONS & deprecation](articles/conditional-requests.md) — `WithETag`, preconditions, `Allow`, and deprecation headers.
- [The typed client](articles/client.md) — `CairnClient`, `Resource<T>`, following links, and submitting forms.
- [OpenAPI & Swagger](articles/openapi.md) — documenting hypermedia responses.
- [Analyzers & generated routes](articles/route-safety.md) — `CAIRN001`–`CAIRN003` and the `Routes.*` catalog.
- [Diagnostics & observability](articles/diagnostics.md) — logged warnings, metrics, and tracing.
- [Testing](articles/testing.md) — asserting on links and affordances.
- [Packages](articles/packages.md) — the full package family in detail.
