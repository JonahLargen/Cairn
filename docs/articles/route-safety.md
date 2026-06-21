# Route safety: analyzers & generated routes

Cairn references endpoints by their route name: `LinkTarget.Route("get-order")` resolves to a URL at runtime using the name registered with `.WithName(...)` (minimal APIs) or a controller route attribute (`[HttpGet(Name = "...")]`, `[Route("...", Name = "...")]`). A misspelled or stale route name is a latent runtime failure rather than a compile error.

The analyzer, code fix, and source generator close that gap. They run during the build with no configuration:

- **CAIRN001** warns when a `LinkTarget.Route("name")` call references a route name that no endpoint or controller declares.
- **CAIRN002** warns when two distinct route names collide in the generated `Routes` catalog.
- The **`Routes` source generator** emits a strongly-typed `Routes.*` catalog so links can be written without string literals.

## CAIRN001: unknown route name

The analyzer collects every route name declared in the compilation — from `.WithName("name")` invocations and from named controller route attributes (`[Route]`, `[HttpGet]`, `[HttpPost]`, `[HttpPut]`, `[HttpDelete]`, `[HttpPatch]`, `[HttpHead]`, `[HttpOptions]`, with or without the `Attribute` suffix). It then checks every `LinkTarget.Route("...")` call against that set.

Given a named endpoint:

```csharp
app.MapGet("/orders/{id}", GetOrder).WithName("get-order");
```

a link that misspells the name is flagged:

```csharp
// CAIRN001: No endpoint or controller route is named 'get-ordr'
// (did you mean 'get-order'?), so the link will not resolve at runtime
b.Self(LinkTarget.Route("get-ordr", new { id = order.Id }));
```

The diagnostic is reported at compilation end with severity **Warning**. The message includes a closest-match hint when one exists, computed by Levenshtein distance over the known names; a suggestion is offered only when the edit distance is 2 or less.

If the compilation declares no route names at all, CAIRN001 reports nothing: the names are assumed to live in another project, so the analyzer stays silent rather than producing false positives.

Only the first argument of `LinkTarget.Route(...)` is examined, and only when it is a string literal. Names built from variables or constants are not analyzed.

### Code fix

When CAIRN001 carries a suggestion, the accompanying code fix offers **Change route name to '<suggestion>'**, replacing the string literal with the closest matching declared name. The fix is batch-fixable, so it can be applied across a document, project, or solution at once.

## CAIRN002: route name collision

The source generator turns each route name into a C# method name by capitalizing letter/digit runs and dropping other characters (for example `get-order` becomes `GetOrder`). Two different route names can reduce to the same method name. When that happens, the second name cannot be emitted, and the generator reports:

```text
CAIRN002: Route name 'get_order' was not added to the Routes catalog
because it maps to the same method name 'GetOrder' as route 'get-order'
```

Only one of the colliding names ends up in the catalog; the other is dropped, and the warning makes the loss visible. The same name appearing on multiple endpoints does not collide — only distinct names that share a generated method name do. Rename one of the route names to resolve it.

## The generated `Routes` catalog

The source generator emits a `public static partial class Routes` in the `Cairn` namespace, with one method per route name. Each method returns a `LinkTarget` and takes a parameter for every route template token.

For these endpoints:

```csharp
app.MapGet("/orders/{id:int}", GetOrder).WithName("get-order");
app.MapGet("/orders", ListOrders).WithName("list-orders");
```

the generator produces:

```csharp
namespace Cairn
{
    public static partial class Routes
    {
        /// <summary>Route to the 'get-order' endpoint.</summary>
        public static global::Cairn.LinkTarget GetOrder(int id)
            => global::Cairn.LinkTarget.Route("get-order", new { id });

        /// <summary>Route to the 'list-orders' endpoint.</summary>
        public static global::Cairn.LinkTarget ListOrders()
            => global::Cairn.LinkTarget.Route("list-orders", null);
    }
}
```

Use the catalog in a link configuration instead of repeating the route name and shaping the values object by hand:

```csharp
b.Self(Routes.GetOrder(order.Id));
b.Link(IanaLinkRelations.Collection, Routes.ListOrders());
```

### Parameter types

Route parameters are extracted from the endpoint template and typed from their inline constraint:

| Constraint | Generated type |
| --- | --- |
| `int` | `int` |
| `long` | `long` |
| `bool` | `bool` |
| `double` | `double` |
| `float` | `float` |
| `decimal` | `decimal` |
| `guid` | `global::System.Guid` |
| `datetime` | `global::System.DateTime` |
| (none / other) | `string` |

So `/orders/{id:int}` yields `GetOrder(int id)` and `/customers/{key:guid}` yields a `System.Guid` parameter. Optional markers and inline default values (`{id:int?}`, `{id=5}`) are stripped from the parameter name and constraint; catch-all markers (`{*rest}`) are handled likewise. A route parameter whose name is a C# keyword is escaped (for example `@class`), and a name that repeats across a group prefix and the endpoint template is emitted only once.

### Sources and grouping

The generator builds the catalog from both:

- **Minimal-API endpoints** — `.WithName("name")` in a `MapGet`/`MapPost`/`MapPut`/`MapDelete`/`MapPatch`/`MapMethods` chain. Inline `MapGroup("/prefix")` segments in the same fluent chain are folded into the template, so their route parameters are included. A group prefix bound to a local variable (`var g = app.MapGroup(...); g.MapGet(...)`) is not visible to the generator, so its parameters are not picked up.
- **Controllers** — `[HttpGet(Name = "name")]` / `[Route("...", Name = "name")]` action attributes, combined with the controller's own `[Route("prefix")]` template. An action template rooted with `/` or `~/` overrides the controller prefix.

If no named routes exist in the compilation, no `Routes` class is generated.

## Related

- [Link configurations](link-configs.md) — building links with `LinkTarget.Route` and service-aware targets.
- [Getting started](getting-started.md) — registering Cairn and naming endpoints.
- [Packages](packages.md) — where the analyzers and generator ship.
