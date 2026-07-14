# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Cairn is an **opt-in, low-friction HATEOAS library for .NET** — it adds hypermedia (links and
affordances) to ASP.NET Core APIs without touching the DTOs.

## Commands

All commands operate on the XML solution `Cairn.slnx` and run from the repo root. This is exactly what
CI runs, so a clean local run means a green build.

```sh
dotnet restore Cairn.slnx
dotnet build Cairn.slnx --configuration Release --no-restore
dotnet test  Cairn.slnx --configuration Release --no-build          # full suite
```

- **Prerequisites:** the .NET 10 SDK builds every target (pinned in `global.json`, floor `10.0.100`,
  `rollForward: latestMinor`). Running the test suite additionally needs the **.NET 8 and .NET 9 runtimes**,
  because test projects multi-target and execute on all three TFMs.
- **Warnings are errors repo-wide** (`TreatWarningsAsErrors`) — including analyzer, XML-doc, and
  trim/AOT-analysis warnings. Fix them; don't suppress without a justification.

**Run a single test project / test:**

```sh
dotnet test tests/Cairn.AspNetCore.Tests/Cairn.AspNetCore.Tests.csproj -c Release
dotnet test Cairn.slnx -c Release --filter "FullyQualifiedName~ClassName.MethodName"
dotnet test tests/Cairn.Core.Tests -c Release -f net10.0          # pin one TFM for a faster loop
```

**Coverage** (CI gate is 95% line *and* branch, generated code excluded):

```sh
dotnet test Cairn.slnx -c Release --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory coverage
```

**Run / build the rest:**

```sh
dotnet run -c Release --project samples/Cairn.Sample.Api          # runnable sample API (Explorer at /explorer)
dotnet run -c Release --project benchmarks/Cairn.Benchmarks       # BenchmarkDotNet suite
dotnet pack Cairn.slnx -c Release --output artifacts              # validates packaging + public API surface
./eng/test-template.sh artifacts                                  # scaffold `dotnet new cairn-api` against just-built packages
docfx docs/docfx.json                                             # build the DocFX site
```

## Core architecture — where links live

**Primary mechanism: a System.Text.Json contract modifier** (`IJsonTypeInfoResolver` /
`DefaultJsonTypeInfoResolver.Modifiers`) projects a `_links` member into the response **without
modifying the DTO**. This keeps records/POCOs clean and works on types the user does not own. The
modifier lives in `src/Cairn.AspNetCore/Internal/CairnLinkInjectionModifier.cs`.

The modifier's `Get` delegate is **synchronous**, so it cannot `await` authorization. This is resolved
with a **two-stage design**:

1. **Async compute stage** — a Minimal-API `IEndpointFilter` (or MVC `IAsyncResultFilter`) runs with full
   DI, awaits `IAuthorizationService.AuthorizeAsync` and inspects entity state, builds a `LinkSet`, and
   stashes it reference-keyed (`CairnLinkRecorder` → `CairnLinkStore`, backed by `HttpContext.Items`).
2. **Sync emit stage** — the contract modifier reads that `LinkSet` back (via `IHttpContextAccessor`) and
   writes `_links`/`_actions`/`_templates`.

A literal `Resource<T>` / `IResource` base class is offered **only as an opt-in escape hatch**, never the
default. Output-buffer–rewriting middleware is explicitly avoided.

**Central design challenge:** correlating the filter-computed `LinkSet` to the exact instance being
serialized, across `Results<Ok<T>>` wrappers, collection and paged envelopes, polymorphic/nested DTOs, and
streaming responses. **Streaming (`IAsyncEnumerable<T>`) intentionally gets no links** — hypermedia is
computed before serialization and a stream can't be enumerated twice; a one-time warning is logged.

**Layering:** `Cairn.Core` is the transport-agnostic engine (`LinkEngine`, `LinkConfig<T>`, `LinkSet`,
`LinkTarget`, `Affordance`, `IanaLinkRelations`) with **no web dependency**. `Cairn.AspNetCore` supplies the
ASP.NET Core integration — the endpoint extensions (`CairnEndpointExtensions`, i.e. `.WithLinks()`), the
`[CairnLinks]` MVC attribute, format negotiation, pagination, and everything under `Internal/`. When
tracing behavior, the public entry points are the top-level `Cairn*` files in `src/Cairn.AspNetCore`; the
machinery is under `src/Cairn.AspNetCore/Internal`.

## Other design decisions

- **Media types / formats:** three built-in shapes selected by `Accept` negotiation — a flat inline
  `{ href, method }` default (`_actions`), **HAL** (`application/hal+json`), and **HAL-FORMS**
  (`application/prs.hal-forms+json`, `_templates`, with form fields derived from `DataAnnotations`). All
  are implemented. Additional formats (e.g. Siren) plug in via `IHypermediaFormatter` and join the same
  negotiation, rather than being baked in. `DefaultFormat = HypermediaFormat.None` makes hypermedia
  opt-in *by the client* (links only when the `Accept` header asks for a hypermedia type).
- **Affordances authorize.** An action is advertised only when its `When(...)` state condition holds **and**
  the caller satisfies the same ASP.NET Core authorization policy that guards the action
  (`RequireAuthorization`), memoized per request via `AuthorizationPolicyLinkAuthorizer`.
- **Strict vs lax** governs link-resolution *failure handling*, not how much hypermedia is emitted. Lax
  (default): an unresolved link is silently omitted, keeping responses safe. Strict (suited to CI/dev):
  unresolved links throw, a self link is required, relations are validated, and duplicates are errors.
- **Route safety without magic strings.** The bundled Roslyn analyzers (`CAIRN001` unknown route name,
  `CAIRN002` `.WithLinks()` on a type with no `LinkConfig<T>`), code fixes, and a source generator (typed
  `Routes.*` catalog) give compile-time link safety and zero reflection on the hot path.
- **AOT-friendly.** Every shippable library is trim/Native-AOT-analyzed (`IsAotCompatible`); reflection-based
  conveniences are annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`.

## Repository layout

`Cairn.slnx` is the SDK-10 XML solution. Repo-wide MSBuild config cascades through `Directory.Build.props`
(nullable, implicit usings, analyzers, warnings-as-errors, authorship) and `Directory.Packages.props`
(**Central Package Management — all package versions live here as exact `[x.y.z]` pins**; never inline a
version in a csproj). Per-tree `Directory.Build.props` files add packaging/XML-docs for `src`, and
`IsPackable=false` for `tests`/`samples`.

```
src/
  Cairn.Core/               transport-agnostic model + engine. No web dependency.        net8/9/10
  Cairn.AspNetCore/         ASP.NET Core integration; bundles the analyzers/generator.   net8/9/10
  Cairn.Client/             typed client that walks links (Traverson-style traversal).   net8/9/10
  Cairn.Testing/            framework-agnostic assertions (throws CairnAssertionException). net8/9/10
  Cairn.Swashbuckle/        hypermedia in Swashbuckle Swagger docs.                       net8/9/10
  Cairn.OpenApi/            hypermedia in Microsoft.AspNetCore.OpenApi docs.              net10 only
  Cairn.AspNetCore.Explorer/ embedded HAL Explorer UI (UseCairnExplorer()).              net8/9/10
  Cairn.Mcp/                exposes affordances as Model Context Protocol tools.          net8/9/10
  Cairn.Analyzers/          Roslyn analyzers      ─┐ ship *inside* Cairn.AspNetCore       netstandard2.0
  Cairn.CodeFixes/          analyzer code fixes    ├ under analyzers/dotnet/cs;           netstandard2.0
  Cairn.SourceGenerators/   Routes.* generator    ─┘ no packages of their own.            netstandard2.0
tests/        xUnit (+ FsCheck.Xunit property tests, Microsoft.AspNetCore.TestHost integration tests)
samples/      Cairn.Sample.Api — runnable minimal-API sample and injection-mechanic spike
benchmarks/   Cairn.Benchmarks — BenchmarkDotNet
templates/    Cairn.Templates — `dotnet new cairn-api` scaffold
docs/         DocFX site → GitHub Pages
eng/          build/test scripts (e.g. test-template.sh)
```

**Target frameworks:** shippable libraries multi-target `net8.0;net9.0;net10.0` (net8 is the LTS floor);
the Roslyn components are `netstandard2.0` (they run inside the compiler); `Cairn.OpenApi` is `net10.0`-only
(it builds on the .NET 10 schema-transformer pipeline). There is no multi-target `#if` in application code —
per-TFM differences are handled with conditioned MSBuild `ItemGroup`s and package floors.

## Conventions

- **Namespaces:** `Cairn` for core types (e.g. `Cairn.Link`); `Cairn.AspNetCore`, `Cairn.Testing`, etc.
- **API vocabulary:** trail language — a link is a "marker" (`.Self(...)`/`.Mark(...)`); an API's entry
  point is its "trailhead." (A cairn is a stack of stones marking a trail — hypermedia placed only where it
  guides.)
- **Public surface is documented** — XML doc comments are required on `src` (warnings-as-errors enforces it).
- **Versioning is tag-driven via MinVer** — a `vX.Y.Z` tag builds exactly `X.Y.Z`; every commit after builds
  `X.Y.(Z+1)-preview.0.<height>`. Nothing is hand-edited; releasing is pushing a tag (see `RELEASING.md`).
  Do not bump a version in a PR.
- **Public API is validated on pack** (`EnablePackageValidation` against `PackageValidationBaselineVersion`)
  — an accidental binary-breaking change fails `dotnet pack`. Intentional breaks are a major-version decision.
- **Assemblies are deliberately not strong-named** — PRs adding strong naming are declined (see
  `CONTRIBUTING.md`).

## Working agreements

- Commit or push only when the maintainer asks.
- Keep all repository content professional and appropriate for a public, open-source project.
- Add/update tests for behavior changes and exercise every branch — CI fails under 95% line **or** branch
  coverage. Update `docs/articles/` and XML docs when public behavior changes.
