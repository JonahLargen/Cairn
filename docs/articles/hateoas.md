# What is HATEOAS?

If you already know what hypermedia, link relations, and affordances are, skip straight to [Getting started](getting-started.md). This page is the five-minute version for everyone else — what the terms mean, what the JSON looks like, and why you'd want any of it.

## The problem: clients that know too much

Here is an ordinary API response:

```json
{ "id": 42, "status": "Pending" }
```

It's honest data, but everything a client needs to *act* on it lives somewhere else:

- **How do I fetch this order again?** The client builds `"/orders/" + id` from knowledge baked into its source code.
- **Can this order be cancelled?** The client re-implements the server's rule: *orders are cancellable while pending*.
- **May this user cancel it?** The client re-implements the server's authorization logic, or shows a button that fails when clicked.

Every client — the web app, the mobile app, the partner integration — duplicates that knowledge. When the server changes a URL, a state rule, or a permission, every copy is silently wrong until someone notices.

## The idea: put the knowledge in the response

*HATEOAS* — **H**ypermedia **A**s **T**he **E**ngine **O**f **A**pplication **S**tate — is the REST principle that the response itself should tell the client where it can go and what it can do next. The same order, served as hypermedia:

```json
{
  "id": 42,
  "status": "Pending",
  "_links": {
    "self": { "href": "https://api.example.com/orders/42" },
    "customer": { "href": "https://api.example.com/customers/7" }
  },
  "_actions": {
    "cancel": { "href": "https://api.example.com/orders/42/cancel", "method": "POST" }
  }
}
```

Two kinds of information have been added:

- **Links** answer *"where can I go from here?"* Each entry under `_links` is keyed by a **relation** (or *rel*) — a name describing how the target relates to this resource. `self` is the resource's own canonical URL; `customer` points to a related resource; a collection page would add `next` and `prev`.
- **Affordances** answer *"what can I do right now?"* Each entry under `_actions` describes a valid state transition: a target URL and an HTTP method (and, in richer formats, the shape of the request body).

The crucial part is what happens when state changes. Fetch the same order after it ships:

```json
{
  "id": 42,
  "status": "Shipped",
  "_links": {
    "self": { "href": "https://api.example.com/orders/42" },
    "customer": { "href": "https://api.example.com/customers/7" }
  }
}
```

The `cancel` action is gone — because it's no longer valid. The client never needed the rule; it only needed to check whether the action was offered. That's what "engine of application state" means: **the server drives what's possible; the client follows.**

The web itself works this way. A browser doesn't hardcode your bank's URLs or know when a transfer is allowed — it renders whatever links and forms the current page offers. HATEOAS applies the same mechanic to JSON APIs.

## What clients gain

- **"Should I show the button?" becomes one line.** A UI renders a Cancel control if `_actions.cancel` is present. State rules and permissions are evaluated once, on the server — the only place that actually knows.
- **URLs stop being a contract.** Clients that follow `self`, `next`, and `customer` links keep working when routes are restructured; clients that build URLs from templates break.
- **Navigation replaces construction.** Paging is "follow `next` until it disappears", not query-string arithmetic.
- **Forms can be discovered.** With [HAL-FORMS](affordances-and-forms.md), an action carries its field definitions — names, types, required flags, allowed values — so a client can render and validate a form it has never seen.

## The vocabulary

These terms come up throughout the documentation:

| Term | Meaning |
| --- | --- |
| **Resource** | The thing a response represents — an order, a customer, a page of results. In Cairn, any DTO with a registered configuration. |
| **Link** | A pointer from one resource to a URL, labeled with a relation. *Where you can go.* |
| **Relation (rel)** | The name of a link's meaning: `self`, `next`, `collection`, `customer`. [RFC 8288](https://datatracker.ietf.org/doc/html/rfc8288) registers common ones; you can also define your own. |
| **Affordance** | An available operation on a resource: relation name, target URL, HTTP method, optionally an input schema. *What you can do.* Cairn emits these as `_actions` (default format) or `_templates` (HAL-FORMS). |
| **Embedded resource** | A related resource included inline (under `_embedded`), decorated with its own links, so the client saves a round trip. |
| **Wire format** | The JSON convention the hypermedia is written in. Cairn ships a flat default shape, [HAL](https://datatracker.ietf.org/doc/html/draft-kelly-json-hal), and [HAL-FORMS](https://rwcbook.github.io/hal-forms/), negotiated by `Accept` header. |

## How the concepts map to Cairn

| Concept | In Cairn |
| --- | --- |
| Declare a resource's hypermedia | A [`LinkConfig<T>`](link-configs.md) class, separate from the DTO |
| Add a link | `builder.Self(...)`, `builder.Link("rel", ...)` |
| Add an affordance | `builder.Affordance("name", ...).Post()` |
| Offer an action only in certain states | [`.When(order => order.Status == OrderStatus.Pending)`](link-configs.md) |
| Offer an action only to authorized callers | [`.RequireAuthorization("Policy")`](link-configs.md) |
| Describe an action's input form | [`.Accepts<TInput>()`](affordances-and-forms.md) (HAL-FORMS) |
| Choose the wire format | [`Accept` negotiation or `.WithHypermediaFormat(...)`](formats.md) |
| Turn hypermedia on for an endpoint | `.WithLinks()` (minimal APIs) or `[CairnLinks]` ([controllers](controllers.md)) |

## Do I need this everywhere?

No — and Cairn is built on that answer. Hypermedia earns its keep on resources that behave like **state machines** (orders, approvals, subscriptions), on APIs that back **permission-aware UIs**, and on responses clients **navigate** (pages, search results, workflows). It adds little to an internal CRUD endpoint whose only client is generated from an OpenAPI spec.

That's why Cairn is opt-in per endpoint: add `.WithLinks()` where hypermedia guides someone, and leave the rest of your API exactly as it was.

## Next

- [Getting started](getting-started.md) — build your first linked endpoint and see the responses.
- [Wire formats & negotiation](formats.md) — the default shape, HAL, and HAL-FORMS side by side.
- [Affordances & HAL-FORMS](affordances-and-forms.md) — actions, conditions, authorization, and forms.
