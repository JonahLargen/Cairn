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

The affordance above renders (HAL-FORMS) as:

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

Each public, readable, non-indexer instance property of the input type becomes one template property. The property `name` is the property's name converted to camelCase. The remaining attributes are mapped from `System.ComponentModel` and `System.ComponentModel.DataAnnotations` annotations; nullable wrappers are unwrapped before type detection.

| Source | HAL-FORMS field | Notes |
| --- | --- | --- |
| `[Required]` | `required: true` | omitted when absent |
| `[Display(Name = ...)]` | `prompt` | from `Display.GetName()` |
| `[Display(Prompt = ...)]` | `placeholder` | from `Display.GetPrompt()` |
| `[Editable(false)]` or `[ReadOnly(true)]` | `readOnly: true` | |
| enum-typed property | `options.inline` | one entry per enum name; `prompt` and `value` are both the name |
| `[Range(min, max)]` | `min` / `max` | bounds converted to numbers |
| `[StringLength]` or `[MaxLength]` | `maxLength` | `StringLength.MaximumLength` preferred, else `MaxLength.Length` |
| `[RegularExpression(pattern)]` | `regex` | the pattern |
| `[EmailAddress]` | `type: "email"` | takes precedence over the inferred type |

### Field type mapping

When no `[EmailAddress]` attribute is present, `type` is inferred from the unwrapped property type:

| CLR type | `type` |
| --- | --- |
| `string` | `text` |
| `bool` | `checkbox` |
| `DateOnly` | `date` |
| `DateTime`, `DateTimeOffset` | `datetime` |
| `TimeOnly` | `time` |
| `int`, `long`, `short`, `byte`, `uint`, `ulong`, `ushort`, `sbyte`, `decimal`, `double`, `float` | `number` |
| any other type | (no `type` emitted) |

Properties and their derived schema are cached per input type, so the annotations on a given type are read once.

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

`Fields("create")` returns each field as an `AffordanceField` (its `Name`, `Prompt`, `Required`, `ReadOnly`, `Type`, `Placeholder`, `Regex`, `MaxLength`, `Min`, `Max`, and `Options`) so a caller can build the request body before invoking. See [The typed client](client.md) for `Resource<T>`, `AffordanceField`, and `InvokeAsync`.

## See also

- [Wire formats & negotiation](formats.md) — how Default and HAL-FORMS emit `_templates`.
- [Link configurations](link-configs.md) — the shared builder, conditions, and authorization.
- [Error responses & problem details](error-responses.md) — attaching actions to problem documents.
