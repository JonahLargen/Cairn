# Route safety: analyzers & generated routes

Cairn references endpoints by their route name: `LinkTarget.Route("get-order")` resolves to a URL at runtime using the name registered with `.WithName(...)` (minimal APIs) or a controller route attribute (`[HttpGet(Name = "...")]`, `[Route("...", Name = "...")]`). A misspelled or stale route name is a latent runtime failure rather than a compile error — and so is an endpoint that opts in to links for a type nothing configures.

The analyzers, code fix, and source generator close those gaps. They run during the build with no configuration:

- **CAIRN001** warns when a `LinkTarget.Route("name")` or `LinkTarget.RouteTemplate("name")` call references a route name that no endpoint or controller declares.
- **CAIRN002** warns when a `.WithLinks()` endpoint or `[CairnLinks]` controller action returns a type that has no `LinkConfig<T>` in the compilation — the classic silent no-op.
- **CAIRN003** warns when two distinct route names collide in the generated `Routes` catalog.
- **CAIRN004** warns when a route name maps to a method name the catalog cannot declare and is emitted under a `_` prefix.
- The **`Routes` source generator** emits a strongly-typed `Routes.*` catalog so links can be written without string literals.

## CAIRN001: unknown route name

The analyzer collects every route name declared in the compilation — from `.WithName("name")` invocations, from conventional routes (`MapControllerRoute("name", ...)` / `MapAreaControllerRoute("name", ...)`), and from named controller route attributes (`[Route]`, `[HttpGet]`, `[HttpPost]`, `[HttpPut]`, `[HttpDelete]`, `[HttpPatch]`, `[HttpHead]`, `[HttpOptions]`, with or without the `Attribute` suffix). It then checks every `LinkTarget.Route("...")` and `LinkTarget.RouteTemplate("...")` call against that set.

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

The diagnostic has severity **Warning** and is reported as each `Route` call is analyzed (against a route-name index built once per compilation), so it shows up live in the IDE with the code fix's lightbulb attached. The message includes a closest-match hint when one exists, computed by Levenshtein distance over the known names; a suggestion is offered only when the edit distance is 2 or less.

If the compilation declares no route names at all, CAIRN001 reports nothing: the names are assumed to live in another project, so the analyzer stays silent rather than producing false positives.

Route names are resolved as compile-time constants on both sides, so names declared or referenced through a `const` field, `nameof(...)`, or constant concatenation are analyzed just like string literals — `[HttpGet("{id:int}", Name = nameof(GetOrder))]` counts as a declaration, and `LinkTarget.Route(RouteNames.GetOrder)` is checked. Only genuinely runtime values (an interpolated `$"GetOrder{id}"`, a variable) are skipped. Arguments are matched to parameters by name as well as position, so a reordered named-argument call (`LinkTarget.Route(routeValues: values, routeName: "get-order")`) is validated too.

Both sides are bound semantically. A look-alike `LinkTarget` type from another namespace is ignored and `using static Cairn.LinkTarget` call sites are still checked. On the declaration side, only a `WithName` *builder extension* declares a route name — `LinkTarget.WithName("...")`, which sets a link's HAL `name`, is never mistaken for one.

### Names declared in another project

When endpoints live in a different assembly than the link configurations, declare their names to the analyzer so cross-project references aren't flagged — either in `.editorconfig`:

```ini
[*.cs]
cairn_additional_route_names = get-order, list-orders
```

or as an MSBuild property (`<CairnAdditionalRouteNames>get-order, list-orders</CairnAdditionalRouteNames>`).

### Code fix

When CAIRN001 carries a suggestion, the accompanying code fix offers **Change route name to '<suggestion>'**, replacing the string literal with the closest matching declared name. The fix is batch-fixable, so it can be applied across a document, project, or solution at once.

## CAIRN002: `WithLinks` endpoint with no `LinkConfig`

An endpoint that opts in with `.WithLinks()` (or a controller action under `[CairnLinks]`) but returns a type nothing configures serializes without hypermedia — at runtime that is a [logged warning](diagnostics.md), and at build time this analyzer catches it earlier:

```csharp
// CAIRN002: This hypermedia-enabled endpoint returns 'OrderDto', but no
// LinkConfig<OrderDto> (or a base type's) is declared in this compilation,
// so it will serialize without hypermedia
app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new OrderDto(id)))
   .WithLinks();
```

The analyzer walks the fluent chain from `.WithLinks()` down to the `Map*` call — following a chain broken through a local variable (`var e = app.MapGet(...); e.WithLinks();`) — and inspects the handler's return type. Controller actions are covered symmetrically: a public action carrying `[CairnLinks]` (on the method, its controller, or a base controller) is checked the same way. Return types unwrap `Task<...>`/`ValueTask<...>`, the `TypedResults` result types, `ActionResult<T>`, arrays and common sequence types, and Cairn's paging envelopes (`PagedResource<T>`, `CursorPage<T>`) to find the resource type. A config on a base type counts: a `LinkConfig<OrderDto>` covers a returned `RushOrderDto : OrderDto`.

Both sides are bound to Cairn's own symbols — a `WithLinks` extension or `LinkConfig<T>` base class from another library is ignored rather than analyzed.

Scope and silence rules:

- `.WithLinks()` applied to a whole `MapGroup` is skipped (the group's endpoints can't be enumerated statically).
- If the compilation declares no `LinkConfig<T>` at all, the analyzer stays silent — configs are assumed to live in another project.
- Types configured in another project can be declared to the analyzer in `.editorconfig` (`cairn_additional_configured_types = OrderDto, Contracts.CustomerDto` — simple or fully qualified names) or via an MSBuild property (`<CairnAdditionalConfiguredTypes>OrderDto</CairnAdditionalConfiguredTypes>`), mirroring CAIRN001's escape hatch.

## CAIRN003: route name collision

The source generator turns each route name into a C# method name by capitalizing letter/digit runs and dropping other characters (for example `get-order` becomes `GetOrder`). Two different route names can reduce to the same method name. When that happens, the second name cannot be emitted, and the generator reports:

```text
CAIRN003: Route name 'get_order' was not added to the Routes catalog
because it maps to the same method name 'GetOrder' as route 'get-order'
```

Only one of the colliding names ends up in the catalog; the other is dropped, and the warning makes the loss visible — reported at the losing route name's declaration, so it is navigable from the error list. The same name appearing on multiple endpoints does not collide — only distinct names that share a generated method name do. Rename one of the route names to resolve it.

## CAIRN004: reserved method name

A route name can reduce to a method name the catalog cannot declare: `Routes` itself (a member may not share its enclosing class's name, CS0542) or a member every type inherits from `System.Object` — `Equals`, `GetHashCode`, `ToString`, `GetType`, `ReferenceEquals` (a same-named static method hides the inherited member, CS0108). Instead of emitting a catalog that does not compile, the generator prefixes the method with `_` and reports:

```text
CAIRN004: Route name 'ToString' maps to 'ToString', which is reserved in the
Routes catalog; the method was emitted as '_ToString' instead
```

The link still targets the original route name (`Routes._ToString()` resolves the `ToString` route); rename the route to control the generated method name.

## The generated `Routes` catalog

The source generator emits an `internal static partial class Routes` in the `Cairn` namespace, with one method per route name. Each method returns a `LinkTarget` and takes a parameter for every route template token.

For these endpoints:

```csharp
app.MapGet("/orders/{id:int}", GetOrder).WithName("get-order");
app.MapGet("/orders", ListOrders).WithName("list-orders");
```

the generator produces:

```csharp
namespace Cairn
{
    internal static partial class Routes
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

The catalog is `internal` because it is app-internal by nature: an internal class can't produce duplicate-type errors against a hand-written public `Cairn.Routes` in a referencing project, or CS0433 ambiguity when several assemblies each generate one. It stays `partial`, so it can be extended within the same assembly; if another assembly needs a project's catalog, expose it through a hand-written public wrapper or `InternalsVisibleTo`. When a compilation declares no named routes at all, an empty `Routes` class is still emitted so referencing code keeps compiling.

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
| `min` / `max` / `range` | `long` |
| (none / other) | `string` |

So `/orders/{id:int}` yields `GetOrder(int id)` and `/customers/{key:guid}` yields a `System.Guid` parameter. The numeric range constraints map to `long` because that is how ASP.NET Core evaluates them.

An optional (`{id:int?}`) or defaulted (`{id=5}`) route parameter becomes a nullable parameter with a `null` default — `GetOptionalOrder(int? id = null)` — and is omitted from the route values when not supplied, so the generated link simply leaves the segment to the route's own default handling. Catch-all markers (`{*rest}`) are stripped. A route parameter whose name is a C# keyword is escaped (for example `@class`), and a name that repeats across a group prefix and the endpoint template is emitted only once.

### Sources and grouping

The generator builds the catalog from both:

- **Minimal-API endpoints** — `.WithName("name")` in a `Map`/`MapGet`/`MapPost`/`MapPut`/`MapDelete`/`MapPatch`/`MapMethods`/`MapFallback` chain (the no-pattern `MapFallback(handler)` overload uses ASP.NET's implicit `{*path:nonfile}` template). `MapGroup("/prefix")` segments are folded into the template, so their route parameters are included — whether the group is chained inline or bound to a local variable (`var g = app.MapGroup("/users/{userId:int}"); g.MapGet(...)`).
- **Controllers** — `[HttpGet(Name = "name")]` / `[Route("...", Name = "name")]` action attributes, combined with the controller's own `[Route("prefix")]` template; a controller without one inherits the nearest base controller's `[Route]` prefix, as MVC does. An action template rooted with `/` or `~/` overrides the controller prefix.

Templates are scanned with brace matching, so a constraint that contains braces of its own — `{code:regex(^\d{4}$)}` or the doubled `{{...}}` escape form — neither truncates its parameter nor swallows the parameters after it.

Like the analyzer, the generator resolves names declared through `const` fields and `nameof(...)` on both minimal-API and controller routes.

## Related

- [Link configurations](link-configs.md) — building links with `LinkTarget.Route` and service-aware targets.
- [Diagnostics & observability](diagnostics.md) — the runtime counterpart: logged warnings, metrics, and tracing.
- [Getting started](getting-started.md) — registering Cairn and naming endpoints.
- [Packages](packages.md) — where the analyzers and generator ship.
