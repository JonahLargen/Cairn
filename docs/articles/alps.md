# ALPS profiles

[ALPS](https://datatracker.ietf.org/doc/draft-amundsen-richardson-foster-alps/) (Application-Level Profile Semantics) is a format for describing the *application semantics* of an API — the vocabulary of fields, links, and actions a resource can carry — independent of any single response. Where OpenAPI describes endpoints and payloads, an ALPS profile describes meaning: what `status` is, that `cancel` is an unsafe state transition, which fields that transition accepts. Generic hypermedia clients use profiles to understand an API they were not hand-coded against.

Everything an ALPS profile needs is already in your `LinkConfig<T>` declarations, so Cairn generates the documents for you.

## Serving profiles

Map the ALPS endpoints on the app:

```csharp
app.MapCairnAlps();
```

That serves, for every registered link configuration:

- **An index** at `/alps` (`application/json`) listing each profile: its name, the CLR resource type it describes, and the document's URL.
- **One profile document per resource type** at `/alps/{profile-name}` (`application/alps+json`).

Profile names default to the kebab-cased CLR type name — `OrderDto` becomes `order-dto`, `PagedResource<OrderDto>` becomes `paged-resource-order-dto`. Both the path and the naming are configurable:

```csharp
app.MapCairnAlps(alps =>
{
    alps.Path = "/meta/profiles";
    alps.ProfileName = type => type.Name.ToLowerInvariant();
});
```

Two types that map to the same name (the same type name in different namespaces, say) get deterministic numeric suffixes (`order`, `order-2`), ordered by full type name. `MapCairnAlps` returns an `IEndpointConventionBuilder` covering both endpoints, so conventions like `RequireAuthorization(...)` or output caching apply in the usual way. The endpoints are excluded from OpenAPI descriptions.

## What a profile contains

Each document maps the type's registered configuration onto ALPS descriptors:

- **The resource's serialized fields** become `semantic` descriptors, under the wire names the host's serializer emits (`[JsonPropertyName]` and the naming policy are honored, and Cairn's injected hypermedia sections are not fields). Field names come from the `Microsoft.AspNetCore.Http.Json.JsonOptions` serializer contract — the one minimal APIs serialize with.
- **Declared links** (`Self`, `Link`, `Links`) become `safe` descriptors — links are navigations. A declared `Title` becomes the descriptor's `title`, a `Deprecated(url)` note lands in its `doc`, and a `Profile(uri)` is carried as a `profile` link on the descriptor.
- **Declared affordances** become transition descriptors typed by their HTTP method, per ALPS's protocol-semantics classification: `GET` → `safe`, `PUT`/`DELETE` → `idempotent`, everything else (`POST`, `PATCH`, custom methods) → `unsafe`. The fields of an `Accepts<TInput>` type nest inside the transition as `semantic` descriptors.
- **Declared embeds** (`Embed`/`EmbedMany`) become `semantic` descriptors, and when the child type has its own registered configuration, the descriptor carries a `profile` link to the child's document — so profiles cross-reference each other the way the resources do.

Descriptor ids are unique within a document (they are its fragment identifiers). When a relation collides with a field name — a `customer` link on a resource with a `customer` property — the transition's id is suffixed by kind (`customer-link`, `cancel-action`, `customer-embedded`) and the original relation is kept as the descriptor's `name`. An action input field the document already declares is referenced by fragment (`"href": "#status"`) rather than re-declared.

A profile deliberately describes **every declared transition, including conditional ones**. A link or affordance gated by `When(...)` or `RequireAuthorization(...)` may be absent from any given response, but it is still part of the resource's vocabulary — the profile says what *can* appear; the response says what *does*.

For example, an `OrderDto` whose config declares a `self` link, a state-conditional `cancel` affordance accepting a `CancelOrderInput`, and an embedded customer produces:

```json
{
  "alps": {
    "version": "1.0",
    "doc": { "format": "text", "value": "ALPS profile of OrderDto, generated from its registered Cairn link configuration." },
    "descriptor": [
      { "id": "id", "type": "semantic" },
      { "id": "status", "type": "semantic" },
      { "id": "self", "type": "safe" },
      {
        "id": "cancel", "type": "unsafe", "title": "Cancel this order",
        "descriptor": [ { "id": "reason", "type": "semantic" } ]
      },
      {
        "id": "customer", "type": "semantic",
        "doc": { "format": "text", "value": "Embedded CustomerDto resource." },
        "link": [ { "rel": "profile", "href": "/alps/customer-dto" } ]
      }
    ]
  }
}
```

## Reporting declarations to your own generators

The generation is built on a small public seam in `Cairn.Core`, mirroring the embed reporting the OpenAPI integrations use: a compiled config (from `ILinkConfigProvider.GetConfig`) implements `IDeclarationReportingConfig`, which reports the `DeclaredLink`s and `DeclaredAffordance`s it was built from — relation, title, media type, deprecation, profile URI, HTTP method, input type, and whether the declaration is conditional. If you generate some other description format, you can read the same declarations without building links for an instance.

## Notes

- Profiles are computed once (per request path base, which cross-profile links embed) and cached; the endpoints are cheap after the first hit.
- Everything is derived from what the configuration *declares*. Targets computed per instance — URLs, per-target `LinkTarget` attribute overrides — exist only at build time and are out of a profile's scope by design.
- The documents serialize through a source-generated `JsonSerializerContext`, so `MapCairnAlps` is trim- and Native-AOT-friendly; see [aot.md](aot.md).
