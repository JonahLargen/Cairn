# Trimming & Native AOT

Every Cairn package is built with the .NET trimming, single-file, and Native AOT analyzers enabled (`IsAotCompatible`) and ships trim/AOT compatibility metadata. The core emission pipeline — link configs, the `Routes.*` catalog, link injection through System.Text.Json, HAL and HAL-FORMS output — works in trimmed and Native AOT applications. A small set of reflection-based conveniences does not; each is annotated so the trim analyzer warns you at the call site, and each has a supported alternative described below.

## What works out of the box

- **Link emission.** Cairn's wire types (`_links`, `_actions`, `_templates` payloads) have source-generated JSON metadata ([`CairnJsonContext`](https://github.com/JonahLargen/Cairn/blob/main/src/Cairn.AspNetCore/Internal/CairnJsonContext.cs)) that `AddCairn` combines into the host's resolver chain. Under a source-gen-only `TypeInfoResolver` — the standard Native AOT setup — hypermedia serializes without reflection-based contracts. Your DTOs' contracts come from your own `JsonSerializerContext`, exactly as in any AOT ASP.NET Core app.
- **The generated `Routes.*` catalog.** Generated targets pass route values as `Dictionary<string, object?>`, which ASP.NET Core's `LinkGenerator` consumes without reflection.
- **Typed registration.** `AddLinks<T>(config)` and `LinkConfigRegistry.Add<T>` compile configs statically.
- **HAL-FORMS field schemas.** `Accepts<TInput>()` preserves `TInput`'s public properties under trimming (the type parameter is annotated `DynamicallyAccessedMembers`), so form fields derive correctly. Enum option lists avoid the AOT-unsafe `Enum.GetValues(Type)`.
- **Problem documents, pagination envelopes (`PagedResource<T>` / `CursorPage<T>`), format negotiation, ETags, deprecation metadata.**

## Annotated APIs (warn under trim analysis)

These members carry `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`; calling them from a trim-analyzed project produces a warning because their behavior genuinely depends on members trimming may remove:

| API | Why | Alternative |
| --- | --- | --- |
| `CairnOptions.AddLinksFromAssembly` / `AddLinksFromAssemblyContaining<T>` | Scans the assembly and instantiates configs via reflection. | Register each config with `AddLinks<T>()`. |
| `LinkConfigRegistry.Add(object)` | Compiles through `MakeGenericType` over the runtime resource type. | `Add<T>(LinkConfig<T>)`. |
| Client `FollowAsync(..., object? variables, ...)` overloads (`CairnClient`, `Resource<T>`, `CollectionResource<TItem>`) | An anonymous/POCO variables object is read via reflection. | Pass an `IReadOnlyDictionary<string, object?>` — dictionaries expand without reflection, so the warning does not apply and can be suppressed at your call site. |

## Requirements and caveats in trimmed apps

- **Anonymous-object route values.** Hand-written link targets like `LinkTarget.Route("GetOrder", new { id = o.Id })` rely on reflection over the anonymous type's properties (the same caveat as ASP.NET Core's own `LinkGenerator` object overloads). Prefer the generated `Routes.*` catalog, or pass a `Dictionary<string, object?>`.
- **Client serialization.** `CairnClient` (de)serializes your payload types through the `JsonSerializerOptions` you construct it with. In a trimmed/AOT app, supply options whose `TypeInfoResolver` is source-generated; a missing contract fails with System.Text.Json's descriptive exception.
- **Deferred sequences under Native AOT.** Buffering a deferred `IEnumerable<T>` builds a `List<T>` at runtime. Reference-type elements always work; an exotic value-type element whose `List<T>` instantiation wasn't compiled degrades to the existing "deferred sequence" logged warning. Materializing results yourself (`ToList()`) — already the recommended practice — avoids the path entirely.
- **OpenAPI/Swagger generation** (`Cairn.OpenApi`, `Cairn.Swashbuckle`) describes your API using `ApiExplorer` metadata; whether document generation itself is supported under trimming is determined by the underlying generator (Microsoft.AspNetCore.OpenApi, Swashbuckle), not by Cairn.

## Suppressed analysis, and why it is safe

A few internal paths inspect runtime types in ways the analyzer cannot verify. Each carries an `UnconditionalSuppressMessage` whose justification states the invariant; in summary they detect well-known BCL interfaces (`IEnumerable<T>`, `ICollection<T>`, `IAsyncEnumerable<T>`, `KeyValuePair<,>`) that are preserved on any type the application itself uses as such, and every miss degrades to an already-designed fallback (a logged warning or default treatment) rather than an error.
