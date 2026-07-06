# Project template

`Cairn.Templates` is a `dotnet new` template pack. It scaffolds an ASP.NET Core minimal API that is
already wired for hypermedia — the pieces from [Getting started](getting-started.md) assembled
correctly, so you can run the project and see `_links` and `_actions` on the first request instead of
wiring `AddCairn`, route names, and a `LinkConfig<T>` by hand.

## Install and create

```bash
dotnet new install Cairn.Templates
dotnet new cairn-api -o Orders.Api
cd Orders.Api
dotnet run
```

Then start at the root and follow the links:

```bash
curl http://localhost:5256/
curl http://localhost:5256/orders/42
```

`GET /orders/42` returns the order with its `self`/`collection` links and — because order 42 is
`Pending` — a `cancel` action that disappears once the order ships or is cancelled.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `-f`, `--framework` | `net10.0` | Target framework: `net10.0`, `net9.0`, or `net8.0` (LTS). |
| `--explorer` | `true` | Include the browsable [HAL Explorer](explorer.md) at `/explorer` (Development only). |
| `--openapi` | `false` | Document the hypermedia in Swagger via [`Cairn.Swashbuckle`](openapi.md) and serve Swagger UI in Development. |

```bash
# An LTS project without the explorer, with Swagger docs:
dotnet new cairn-api -o Orders.Api -f net8.0 --explorer false --openapi true
```

## What it generates

A single minimal-API project modelling a small orders resource — the smallest example that still shows
the patterns you reuse in a real API:

| File | What it demonstrates |
| --- | --- |
| `Program.cs` | `AddCairn` with the link configs registered, and endpoints that are each named (`.WithName(...)`) and opted in with `.WithLinks()`. |
| `Root.cs` | The API entry point (`/`) — a home resource whose links let a client discover the rest. |
| `Orders.cs` | Plain DTOs with their hypermedia declared separately in `LinkConfig<T>`, a state-conditional `cancel` affordance, an `_embedded` collection, and a HAL-FORMS `create` form derived from an input type. |
| `Store.cs` | A tiny in-memory store so the actions round-trip. Replace it with your data layer. |

Link targets use the generated `Routes.*` catalog (see [Route safety](route-safety.md)) rather than
magic strings, so a renamed endpoint is a compile error, not a broken link at runtime.

## Template, sample, and guide

Three onboarding paths, each with a different job:

- **This template** *seeds* a new project you own and evolve — it references the published Cairn
  packages and gives you a clean, correct starting point.
- The [sample API](https://github.com/JonahLargen/Cairn/tree/main/samples/Cairn.Sample.Api) is a
  read-only *reference* that exercises more of the surface (controllers, cross-resource links,
  customers) in one place.
- [Getting started](getting-started.md) *teaches* the wiring step by step, so you understand what the
  template generated.

The template pack ships with the same version as the Cairn libraries, and a generated project
references that matching version. See [Packages](packages.md) for the full package list.
