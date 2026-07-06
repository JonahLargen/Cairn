# CairnApi

An ASP.NET Core minimal API wired for [Cairn](https://github.com/JonahLargen/Cairn) hypermedia
(HATEOAS). It models a small orders resource that shows the pieces you'll reuse in a real API:

- **Clean DTOs** (`OrderDto`, `RootDto`) with **link rules declared separately** in `LinkConfig<T>`
  classes — Cairn never modifies your models.
- A **state-conditional affordance**: the `cancel` action appears on a `Pending` order and disappears
  once it ships, because the server owns that rule (`.When(o => o.Status == OrderStatus.Pending)`).
- **Compile-checked routes** via the generated `Routes.*` catalog — no magic route-name strings.
- An **embedded collection** (`_embedded.orders`) and a **create form** derived from `CreateOrderInput`.

## Run it

```bash
dotnet run
```

Then request the root and follow the links:

```bash
curl http://localhost:5256/
curl http://localhost:5256/orders
curl http://localhost:5256/orders/42
```

`GET /orders/42` returns the order with its `self`/`collection` links and — because order 42 is
`Pending` — a `cancel` action. Cancel it and fetch again; the action is gone:

```bash
curl -X POST http://localhost:5256/orders/42/cancel \
  -H "Content-Type: application/json" -d '{"reason":"changed my mind"}'
```

### Wire formats

The same link configuration serves three shapes, chosen by the `Accept` header:

```bash
curl http://localhost:5256/orders/42 -H "Accept: application/json"                    # flat: _links + _actions
curl http://localhost:5256/orders/42 -H "Accept: application/hal+json"                # HAL
curl http://localhost:5256/orders/42 -H "Accept: application/prs.hal-forms+json"      # HAL-FORMS
```

## What's here

| File | Purpose |
| --- | --- |
| `Program.cs` | Registers Cairn (`AddCairn`), the link configs, and the endpoints (each named and opted in with `.WithLinks()`). |
| `Root.cs` | The API entry point (`/`) and its links. |
| `Orders.cs` | The order DTOs, form inputs, and their `LinkConfig<T>` rules. |
| `Store.cs` | A tiny in-memory store so the actions round-trip. Replace with your data layer. |

If you generated this project with the **HAL Explorer** (on by default), browse the API interactively
at `/explorer` while running in Development. If you generated it with **`--openapi`**, the Swagger UI
is at `/swagger`.

## Next steps

- [Getting started](https://jonahlargen.github.io/Cairn/articles/getting-started.html)
- [Link configurations](https://jonahlargen.github.io/Cairn/articles/link-configs.html)
- [Affordances & HAL-FORMS](https://jonahlargen.github.io/Cairn/articles/affordances-and-forms.html)
- [Pagination](https://jonahlargen.github.io/Cairn/articles/pagination.html) ·
  [Testing](https://jonahlargen.github.io/Cairn/articles/testing.html) ·
  [The typed client](https://jonahlargen.github.io/Cairn/articles/client.html)
