# Packages

Cairn ships as a small set of focused NuGet packages. Most applications install `Cairn.AspNetCore` (which brings in `Cairn.Core` and the build-time analyzers); the remaining packages are optional and serve specific roles — consuming an API, testing, or describing it.

## Package reference

| Package | Purpose | Install when |
| --- | --- | --- |
| `Cairn.Core` | Transport-agnostic hypermedia model and engine: links, link relations, and affordances. No ASP.NET dependency. | Usually transitive. Reference it directly only when defining link configurations in a project with no ASP.NET reference. |
| `Cairn.AspNetCore` | ASP.NET Core integration for both minimal APIs (`.WithLinks()`) and MVC controllers (`[CairnLinks]`). Bundles the analyzer, code fixes, and source generator. | The main install for any API that emits hypermedia. |
| `Cairn.Client` | A typed client for consuming Cairn hypermedia APIs: read a resource's value, links, and affordances; navigate by relation; invoke affordances. | In a consumer (service, worker, or test) that calls a Cairn API. |
| `Cairn.Testing` | Test assertion helpers for links and affordances in API responses, built on AwesomeAssertions. | In a test project asserting on hypermedia output. |
| `Cairn.OpenApi` | Surfaces hypermedia links and affordances in the OpenAPI document via `Microsoft.AspNetCore.OpenApi`. | When you generate OpenAPI with `Microsoft.AspNetCore.OpenApi`. |
| `Cairn.Swashbuckle` | Surfaces hypermedia links and affordances in the Swagger/OpenAPI document via an `ISchemaFilter`. | When you generate OpenAPI with Swashbuckle. |

## Cairn.Core

The hypermedia model and resolution engine: `Link`, `LinkRelation`, `IanaLinkRelations`, `Affordance`, `LinkTarget`, `LinkSet`, `EmbeddedResource`, `LinkContext`, and the configuration surface — `LinkConfig<T>`, `ILinkBuilder<T>`, `ILinkSpec<T>`, `IAffordanceSpec<T>`, and `LinkConfigRegistry`. It has no ASP.NET dependency, so link configurations can live in a domain or contracts assembly.

`Cairn.AspNetCore`, `Cairn.Client`, `Cairn.Testing`, `Cairn.OpenApi`, and `Cairn.Swashbuckle` all reference `Cairn.Core`, so it normally arrives transitively. See [Link configurations](link-configs.md) for the builder API.

## Cairn.AspNetCore

The main server package. It wires Cairn into ASP.NET Core through `AddCairn(Action<CairnOptions>)` and exposes both endpoint surfaces:

- Minimal APIs — `.WithLinks()`, `.WithPageLinks`, `.WithCursorLinks`, `.WithHypermediaFormat`.
- MVC controllers — the `[CairnLinks]` attribute.

It also bundles the route-safety tooling — the analyzer (CAIRN001, CAIRN002), the code fixes, and the source generator that produces the `Routes.*` catalog. These ship inside this package as Roslyn components; there is no separate analyzer package to install. Adding `Cairn.AspNetCore` enables them automatically.

```bash
dotnet add package Cairn.AspNetCore
```

```csharp
builder.Services.AddCairn(o => o.AddLinks(new OrderLinks()));
```

See [Getting started](getting-started.md), [Controllers / MVC](controllers.md), and [Analyzers & generated routes](route-safety.md).

## Cairn.Client

A typed consumer client. `CairnClient` reads a resource's value, ETag, links, and affordances, navigates by relation with `FollowAsync`, and invokes affordances with `InvokeAsync`; `Resource<T>` and `ClientResult<T>` model the responses. Register it with `AddCairnClient(Action<CairnClientOptions>)`, which returns an `IHttpClientBuilder`.

```bash
dotnet add package Cairn.Client
```

See [The typed client](client.md).

## Cairn.Testing

Test assertion helpers built on AwesomeAssertions. `HypermediaResponse` plus `.Should()` exposes fluent checks such as `HaveSelfLink`, `HaveLink`, `NotHaveLink`, and `HaveAffordance`.

```bash
dotnet add package Cairn.Testing
```

See [Testing](testing.md).

## Cairn.OpenApi and Cairn.Swashbuckle

Two interchangeable packages that describe hypermedia in your API document — pick the one that matches your OpenAPI generator:

- `Cairn.OpenApi` — `AddCairnHypermedia()` on `OpenApiOptions` (`Microsoft.AspNetCore.OpenApi`).
- `Cairn.Swashbuckle` — `AddCairnHypermedia()` on `SwaggerGenOptions` (Swashbuckle), implemented as an `ISchemaFilter`.

```bash
dotnet add package Cairn.OpenApi
# or
dotnet add package Cairn.Swashbuckle
```

See [OpenAPI & Swagger](openapi.md).

## Targeting

All shippable packages target `net10.0` and require the .NET 10 SDK. The Roslyn components bundled in `Cairn.AspNetCore` target `netstandard2.0`, as Roslyn requires.
