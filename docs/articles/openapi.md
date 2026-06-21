# OpenAPI & Swagger

Cairn keeps hypermedia out of your DTOs at compile time, so a linked resource type carries no `_links`, `_embedded`, or `_actions` members of its own. To make that shape visible in your generated API description, Cairn ships two schema integrations: one for the built-in `Microsoft.AspNetCore.OpenApi` document generator (`Cairn.OpenApi`) and one for Swashbuckle (`Cairn.Swashbuckle`). Both add the hypermedia properties to the schema of every type that has a registered link configuration.

## Microsoft.AspNetCore.OpenApi

Call `AddCairnHypermedia()` on `OpenApiOptions` when registering the document.

```csharp
using Cairn.OpenApi;

builder.Services.AddOpenApi(o => o.AddCairnHypermedia());
```

`AddCairnHypermedia(this OpenApiOptions options)` registers an `IOpenApiSchemaTransformer` and returns the same `OpenApiOptions` instance, so it chains with other transformer registrations.

## Swashbuckle

Call `AddCairnHypermedia()` on `SwaggerGenOptions`.

```csharp
using Cairn.Swashbuckle;

builder.Services.AddSwaggerGen(c => c.AddCairnHypermedia());
```

`AddCairnHypermedia(this SwaggerGenOptions options)` registers a schema filter and returns the same `SwaggerGenOptions` instance.

## What gets documented

Both integrations resolve `ILinkConfigProvider` from dependency injection and look up the schema's type with `GetConfig`. A type with no registered configuration is left untouched; a type with a configuration gains three properties on its schema:

- `_links` — an object keyed by relation. Each value is a link object with `href`, `templated`, `title`, `type`, `name`, `deprecation`, `hreflang`, and `profile`. A relation that resolves to several links is a JSON array of these objects.
- `_embedded` — an object keyed by relation (HAL `_embedded`); each value is a resource or an array of resources.
- `_actions` — an object keyed by action name. Each value is an affordance object with `href`, `method`, and `title`.

The resulting schema fragment for a linked type looks like this:

```json
{
  "_links": {
    "type": "object",
    "additionalProperties": {
      "type": "object",
      "properties": {
        "href": { "type": "string" },
        "templated": { "type": "boolean" },
        "title": { "type": "string" },
        "type": { "type": "string" },
        "name": { "type": "string" },
        "deprecation": { "type": "string" },
        "hreflang": { "type": "string" },
        "profile": { "type": "string" }
      }
    }
  },
  "_embedded": {
    "type": "object",
    "additionalProperties": {}
  },
  "_actions": {
    "type": "object",
    "additionalProperties": {
      "type": "object",
      "properties": {
        "href": { "type": "string" },
        "method": { "type": "string" },
        "title": { "type": "string" }
      }
    }
  }
}
```

The shape mirrors the Default and HAL wire formats. For how those properties are populated at runtime, see [formats.md](formats.md); for the configurations that drive which types are recognized, see [link-configs.md](link-configs.md), [embedded-resources.md](embedded-resources.md), and [affordances-and-forms.md](affordances-and-forms.md).

## Notes

- Only types with a registered link configuration are modified, so unlinked DTOs and input models keep their plain schemas.
- The properties are added to the resource schema alongside its own members; the integration does not remove or rename anything you already declared.
- Both integrations resolve the link configuration provider from dependency injection, so call `AddCairnHypermedia()` after the Cairn services are registered with `AddCairn(...)`. See [getting-started.md](getting-started.md) for the base setup.
