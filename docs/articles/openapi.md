# OpenAPI & Swagger

Cairn keeps hypermedia out of your DTOs at compile time, so a linked resource type carries no `_links`, `_embedded`, `_actions`, or `_templates` members of its own. To make that shape visible in your generated API description, Cairn ships two integrations: one for the built-in `Microsoft.AspNetCore.OpenApi` document generator (`Cairn.OpenApi`) and one for Swashbuckle (`Cairn.Swashbuckle`). Both document the hypermedia properties on every type that has a registered link configuration, and both advertise the negotiable hypermedia media types on the operations that return them.

`Cairn.OpenApi` targets .NET 10 only — it plugs into the `Microsoft.AspNetCore.OpenApi` schema-transformer pipeline, which exists in the shape Cairn builds on only there. On .NET 8/9, use `Cairn.Swashbuckle` instead; see [packages.md](packages.md).

## Microsoft.AspNetCore.OpenApi

Call `AddCairnHypermedia()` on `OpenApiOptions` when registering the document.

```csharp
using Cairn.OpenApi;

builder.Services.AddOpenApi(o => o.AddCairnHypermedia());
```

`AddCairnHypermedia(this OpenApiOptions options)` registers an `IOpenApiSchemaTransformer` (which augments type schemas) and an `IOpenApiOperationTransformer` (which augments operation responses), and returns the same `OpenApiOptions` instance, so it chains with other transformer registrations.

## Swashbuckle

Call `AddCairnHypermedia()` on `SwaggerGenOptions`.

```csharp
using Cairn.Swashbuckle;

builder.Services.AddSwaggerGen(c => c.AddCairnHypermedia());
```

`AddCairnHypermedia(this SwaggerGenOptions options)` registers a schema filter and an operation filter, and returns the same `SwaggerGenOptions` instance.

## What gets documented on schemas

Both integrations resolve `ILinkConfigProvider` from dependency injection and look up the schema's type with `GetConfig`. A type with no registered configuration is left untouched; a type with a configuration gains four properties on its schema:

- `_links` — an object keyed by relation. Each value is a link object with `href`, `templated`, `title`, `type`, `name`, `deprecation`, `hreflang`, and `profile` — or an array of such objects, for a relation carrying several links (the schema models both with `anyOf`).
- `_embedded` — an object keyed by relation (HAL `_embedded`); each value is a resource or an array of resources.
- `_actions` — an object keyed by action name. Each value is an affordance object with `href`, `method`, and `title`.
- `_templates` — the HAL-FORMS projection: an object keyed by template name, each carrying `method`, `target`, `title`, `contentType`, and `properties`.

The shape mirrors the wire formats. For how those properties are populated at runtime, see [formats.md](formats.md); for the configurations that drive which types are recognized, see [link-configs.md](link-configs.md), [embedded-resources.md](embedded-resources.md), and [affordances-and-forms.md](affordances-and-forms.md).

### Pagination envelopes

The [pagination](pagination.md) envelopes are documented too: `PagedResource<T>`, `CursorPage<T>`, types implementing the paging interfaces, and envelopes adapted via `AddPaging`/`AddCursorPaging` all gain a pagination `_links` schema with named relation properties — `self`/`first`/`prev`/`next`/`last` for offset pages, `self`/`next`/`prev` for cursor pages. (The adapted registrations reach the document generators through the `IPaginationEnvelopeProvider` service `AddCairn` registers.)

## What gets documented on operations

The operation transformer/filter mirrors each Cairn-linked `application/json` response onto the negotiable hypermedia media types — `application/hal+json` and `application/prs.hal-forms+json` — reusing the JSON schema, so consumers can see that the endpoint answers content negotiation.

A **bare collection response is deliberately not advertised** with the HAL media types: a JSON array is not a HAL document, and on the wire it stays `application/json` (its elements still carry `_links`). Only configured resource types and pagination envelopes get the extra media types — matching exactly what the [content-type relabeling](formats.md) does at runtime.

## Notes

- Only types with a registered link configuration are modified, so unlinked DTOs and input models keep their plain schemas.
- The properties are added to the resource schema alongside its own members; the integration does not remove or rename anything you already declared. That includes the hypermedia names themselves: if your DTO declares its own `_links` (or `_embedded`/`_actions`/`_templates`) property, your schema for it is kept — mirroring the wire, where Cairn never overwrites a property the DTO declares.
- Both integrations resolve the link configuration provider from dependency injection, so call `AddCairnHypermedia()` after the Cairn services are registered with `AddCairn(...)`. See [getting-started.md](getting-started.md) for the base setup.
