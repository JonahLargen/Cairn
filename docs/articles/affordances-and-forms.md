# Affordances & HAL-FORMS

An *affordance* is a control that describes how to invoke a state transition on a resource: a name (relation), a target URI, an HTTP method, and — for HAL-FORMS — the shape of the input it accepts. Where a [link](link-configs.md) tells a client where it can *go*, an affordance tells a client what it can *do*.

Affordances are declared on the same `ILinkBuilder<T>` used for links. They surface in the [Default and HAL-FORMS wire formats](formats.md); plain HAL has no concept of an action, so affordances are omitted there.

## Declaring an affordance

`ILinkBuilder<T>.Affordance(name, target)` adds an action with the given relation name and target. The default method is `POST`.

```csharp
public sealed class OrderLinks : LinkConfig<Order>
{
    public override void Configure(ILinkBuilder<Order> builder)
    {
        builder.Self(o => LinkTarget.Route("GetOrder", new { id = o.Id }));

        builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id }));
    }
}
```

As with links, the target callback has three overloads: `Func<T, LinkTarget>`, a service-aware `Func<T, LinkContext, LinkTarget>`, and an async `Func<T, LinkContext, ValueTask<LinkTarget>>`. See [Link configurations](link-configs.md) for `LinkContext` (its `Services` and `CancellationToken`) and for `LinkTarget.Route` / `LinkTarget.Uri`.

## Verb helpers

`IAffordanceSpec<T>` exposes the HTTP method as fluent helpers. `Method(httpMethod)` sets an arbitrary verb; the five common verbs have shorthands:

```csharp
builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id })).Delete();
builder.Affordance("replace", o => LinkTarget.Route("PutOrder", new { id = o.Id })).Put();
builder.Affordance("update", o => LinkTarget.Route("PatchOrder", new { id = o.Id })).Patch();
builder.Affordance("reorder", o => LinkTarget.Route("Reorder", new { id = o.Id })).Get();
builder.Affordance("checkout", o => LinkTarget.Route("Checkout", new { id = o.Id })).Post();
```

| Helper | Method |
| --- | --- |
| `Get()` | `GET` |
| `Post()` | `POST` (the default) |
| `Put()` | `PUT` |
| `Patch()` | `PATCH` |
| `Delete()` | `DELETE` |
| `Method(httpMethod)` | any verb |

`Title(title)` sets a human-readable label for the action.

## Conditions and authorization

Affordances support the same conditional and authorization gates as links:

- `When(Func<T, bool>)`, `When(Func<T, LinkContext, bool>)`, and `When(Func<T, LinkContext, ValueTask<bool>>)` include the affordance only when the predicate holds.
- `RequireAuthorization(policy)` includes it only when the caller satisfies the named authorization policy; `RequireAuthorization()` requires the default policy (an authenticated user, by default).
- `RequireAuthorization(policy, o => resource)` evaluates the policy against a resource (ASP.NET Core resource-based authorization) so the decision can be per-item — "may this caller cancel *this* order?" See [Link configurations](link-configs.md#resource-based-authorization).

```csharp
builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id }))
    .Delete()
    .When(o => o.Status == OrderStatus.Open)
    .RequireAuthorization("CanManageOrders");
```

See [Link configurations](link-configs.md) for details on how conditions and policies are evaluated.

## HAL-FORMS templates

In the HAL-FORMS format an affordance projects into a `_templates` entry carrying its `method`, `target`, optional `title`, a `contentType`, and a list of `properties` describing the input fields. Cairn derives those properties from the input type declared with `Accepts<TInput>()`.

```csharp
builder.Affordance("create", _ => LinkTarget.Route("CreateOrder"))
    .Post()
    .Accepts<CreateOrderInput>()
    .Title("Place an order");
```

`Accepts<TInput>()` records the input type; `ContentType(contentType)` sets the template's `contentType` (default `application/json`), for example `ContentType("multipart/form-data")`.

```csharp
public sealed class CreateOrderInput
{
    [Required]
    [Display(Name = "Customer", Prompt = "Customer id")]
    public string CustomerId { get; set; } = "";

    [Range(1, 100)]
    public int Quantity { get; set; }

    [EmailAddress]
    public string? NotifyEmail { get; set; }

    public ShippingSpeed Speed { get; set; }
}

public enum ShippingSpeed { Standard, Express, Overnight }
```

The affordance above renders (HAL-FORMS) as — assuming the resource declares other affordances too; a response whose *only* template this is would key it `default` instead (see [the default template](#the-default-template-asdefault)):

```json
{
  "_templates": {
    "create": {
      "method": "POST",
      "target": "/orders",
      "title": "Place an order",
      "contentType": "application/json",
      "properties": [
        {
          "name": "customerId",
          "prompt": "Customer",
          "required": true,
          "type": "text",
          "placeholder": "Customer id"
        },
        { "name": "quantity", "type": "number", "min": 1, "max": 100 },
        { "name": "notifyEmail", "type": "email" },
        {
          "name": "speed",
          "type": "text",
          "options": {
            "inline": [
              { "prompt": "Standard", "value": "Standard" },
              { "prompt": "Express", "value": "Express" },
              { "prompt": "Overnight", "value": "Overnight" }
            ]
          }
        }
      ]
    }
  }
}
```

### Property derivation

Each serializable instance property of the input type becomes one template property. The property `name` is the property's **wire name under the host's JSON contract** — `[JsonPropertyName]` and the serializer's `PropertyNamingPolicy` (camelCase by default, snake_case if that's what the app binds) — so a generic client can submit exactly the fields the endpoint reads. The remaining attributes are mapped from `System.ComponentModel` and `System.ComponentModel.DataAnnotations` annotations; nullable wrappers are unwrapped before type detection.

| Source | HAL-FORMS field | Notes |
| --- | --- | --- |
| `[Required]` | `required: true` | omitted when absent |
| `[Display(Name = ...)]` | `prompt` | from `Display.GetName()`; localized prompts (`ResourceType`) resolve per request culture |
| `[Display(Prompt = ...)]` | `placeholder` | from `Display.GetPrompt()` |
| `[Editable(false)]` or `[ReadOnly(true)]` | `readOnly: true` | |
| enum-typed property | `options.inline` | one entry per member; `prompt` is the member name, `value` is the wire form the binder accepts — numeric by default, the exact serialized string when a `JsonStringEnumConverter` applies (declared on the enum or added to the serializer options) |
| `[HalFormsOptionsLink(href)]` | `options.link` | *options by reference* — the values are fetched from `href` instead of listed inline; see [Options by reference](#options-by-reference) |
| `[Range(min, max)]` | `min` / `max` | bounds converted to numbers |
| `[StringLength]` or `[MaxLength]` | `maxLength` | `StringLength.MaximumLength` preferred, else `MaxLength.Length` |
| `[StringLength(MinimumLength = ...)]` or `[MinLength]` | `minLength` | omitted when zero |
| `[RegularExpression(pattern)]` | `regex` | the .NET pattern, carried verbatim — see the note below |
| `[EmailAddress]` | `type: "email"` | takes precedence over the inferred type |

> [!NOTE]
> The `regex` value is the `[RegularExpression]` pattern **verbatim** — Cairn does not translate it. HAL-FORMS clients typically validate `regex` with their own engine (a browser or JavaScript client uses ECMAScript regular expressions), and .NET-specific constructs — inline options like `(?i)`, named groups with `(?'name'...)`, balancing groups, `\p{...}` with .NET-only category names, comments in `(?x)` mode — will fail or behave differently there. If non-Cairn clients consume your forms, keep validation patterns to the ECMAScript-compatible subset. Server-side validation still uses the .NET pattern, so the attribute stays authoritative either way.

### Field type mapping

When no `[EmailAddress]` attribute is present, `type` is inferred from the unwrapped property type:

| CLR type | `type` |
| --- | --- |
| `string` | `text` |
| `bool` | (no `type` — described by a two-value `options.inline` list; `checkbox` is not a valid HAL-FORMS type) |
| `DateOnly` | `date` |
| `DateTime`, `DateTimeOffset` | `datetime-local` |
| `TimeOnly` | `time` |
| `int`, `long`, `short`, `byte`, `uint`, `ulong`, `ushort`, `sbyte`, `decimal`, `double`, `float` | `number` |
| any other type | (no `type` emitted) |

Properties and their derived schema are cached per input type and serializer contract — and per UI culture when any prompt is localizable — so the annotations on a given type are read once.

### Options by reference

An `enum` or `bool` property enumerates its selectable values inline (`options.inline`). When the value set is large, dynamic, or already exposed by another endpoint, annotate the property with `[HalFormsOptionsLink]` to emit **options by reference** — a HAL-FORMS `options.link` the client dereferences to fetch the list — instead of an inline enumeration:

```csharp
public sealed class AssignTicketInput
{
    // The client GETs /users to populate the picker, reading each item's "name" and "id".
    [HalFormsOptionsLink("/users", ValueField = "id", PromptField = "name")]
    public string Assignee { get; init; } = "";
}
```

renders the field as:

```json
{
  "name": "assignee",
  "type": "text",
  "options": {
    "link": { "href": "/users" },
    "promptField": "name",
    "valueField": "id"
  }
}
```

`Href` is emitted verbatim — a relative or absolute URI, or a URI template when `Templated = true` (which adds `"templated": true` to the link). It is **not** resolved through the route table: the field schema is computed once per input type and reused across requests, so it holds no per-request URL state. Point it at a stable path (or template) the client can resolve against the response.

HAL-FORMS `options` carry either an inline list or a link, never both, so `[HalFormsOptionsLink]` takes precedence over the automatic inline derivation — an `enum` property with the attribute emits the link, not its members. The optional `Type` (a media-type hint on the link), `ValueField`, and `PromptField` are omitted when unset. The [typed client](client.md) reads the href back as `AffordanceField.OptionsLink`.

## The default template: `AsDefault()`

HAL-FORMS reserves the template key `default` for a resource's primary action. Mark an affordance with `AsDefault()` to emit it under that key:

```csharp
builder.Affordance("update", o => LinkTarget.Route("PutOrder", new { id = o.Id }))
    .Put()
    .Accepts<UpdateOrderInput>()
    .AsDefault();

builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id }));
```

In HAL-FORMS this renders `_templates.default` (the `update` name is dropped from `_templates`) alongside `_templates.cancel`. Other formats are unaffected: the Default format still emits `_actions.update` under its declared name. An affordance literally named `default` (any casing) behaves the same way.

When a response carries **exactly one** template, it is keyed `default` even without `AsDefault()` — a sole template is unambiguously the primary action, and a generic HAL-FORMS client that only looks up the reserved key should find it. As soon as a second affordance emits on the same response, unmarked templates keep their declared names.

An affordance emitted under `default` never shows its declared name in the document, so a curie-prefixed name (e.g. `acme:approve`) does **not** cause the `acme` curie to be advertised for it — there would be no `acme:`-prefixed key for the curie to document.

Because `default` can only mean one thing, registration throws an `InvalidOperationException` if more than one affordance *unconditionally* claims it — two `AsDefault()` calls, or an `AsDefault()` plus an affordance named `default`. Claimants gated with `When(...)` or `RequireAuthorization(...)` are exempt, so mutually exclusive defaults are fine ("approve is the default when pending, reopen when closed"). If gating goes wrong and two claimants emit on the same response anyway, the last one wins on the wire and a warning is logged once per resource type.

## Invoking affordances from a client

The [typed client](client.md) reads affordances back from a response and submits them. `Resource<T>.Fields(name)` exposes the parsed `AffordanceField` list for a named template, and `Resource<T>.InvokeAsync(name, body?, ifMatch?)` sends the request:

```csharp
var result = await client.GetAsync<Order>("/orders/42");
var order = result.Resource!;

if (order.HasAffordance("cancel"))
{
    await order.InvokeAsync("cancel");
}
```

`Fields("create")` returns each field as an `AffordanceField` (its `Name`, `Prompt`, `Required`, `ReadOnly`, `Type`, `Value`, `Placeholder`, `Regex`, `MinLength`, `MaxLength`, `Min`, `Max`, `Step`, `Cols`, `Rows`, and `Options`) so a caller can build the request body before invoking. For form-aware submission — where the client validates the body against those fields *before* sending — use `Resource<T>.SubmitAsync(name, values)` instead. See [The typed client](client.md) for `Resource<T>`, `AffordanceField`, `InvokeAsync`, and `SubmitAsync`.

## See also

- [Wire formats & negotiation](formats.md) — how Default and HAL-FORMS emit `_templates`.
- [Link configurations](link-configs.md) — the shared builder, conditions, and authorization.
- [Error responses & problem details](error-responses.md) — attaching actions to problem documents.
