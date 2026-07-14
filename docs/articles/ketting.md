# Consuming a Cairn API with Ketting (JavaScript)

[Ketting](https://github.com/badgateway/ketting) is the de-facto generic hypermedia client for JavaScript and TypeScript — it runs in the browser and Node, speaks HAL and HAL-FORMS (plus Siren, JSON:API, Collection+JSON, HTML links, and HTTP `Link` headers), and ships [React bindings](https://github.com/badgateway/react-ketting). Because Cairn emits standard HAL and HAL-FORMS, a Cairn API is a Ketting API with **no adapter code on either side**: Ketting asks for HAL-FORMS, Cairn's content negotiation serves it, and links, forms, embedded resources, and problem details all line up.

This page walks through the pairing end to end. Everything on the server side is ordinary Cairn — nothing here is Ketting-specific configuration.

> [!NOTE]
> HAL-FORMS support (the format that carries Cairn's affordances) arrived in **Ketting 7**. Earlier versions still consume Cairn's `_links` and `_embedded` as plain HAL, but won't see `_templates`.

## Why it works: content negotiation

Ketting sends an `Accept` header built from the formats it can parse, in preference order:

```
Accept: application/prs.hal-forms+json;q=1.0, application/hal+json;q=0.9,
        application/vnd.api+json;q=0.8, application/vnd.siren+json;q=0.8,
        application/vnd.collection+json;q=0.8, application/json;q=0.7, text/html;q=0.6
```

Cairn [negotiates per RFC 9110](formats.md#accept-negotiation) across the formats it can emit. `application/prs.hal-forms+json` is an exact match at the highest quality, so every request from Ketting gets **HAL-FORMS**: `_links`, `_embedded`, and affordances as `_templates`, with the response relabeled `Content-Type: application/prs.hal-forms+json`. Ketting reads that content type and parses links and forms accordingly.

Two consequences worth knowing:

- **Opt-in mode costs nothing.** With [`DefaultFormat = HypermediaFormat.None`](formats.md#opt-in-links-only-when-the-client-asks) — where plain `application/json` callers get the bare resource — Ketting still receives full hypermedia, because its `Accept` header names the HAL-FORMS media type explicitly. Ketting is precisely the "hypermedia-aware client that asks" that mode is designed for.
- **Don't force plain HAL on endpoints Ketting invokes actions on.** `.WithHypermediaFormat(HypermediaFormat.Hal)` drops affordances (HAL has no actions — Cairn logs a warning), so `state.action(...)` on the Ketting side finds nothing. Leave negotiation on, or force `HalForms`.

## Links: follow, don't build

Declare links in a [`LinkConfig<T>`](link-configs.md) as usual:

```csharp
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(o => LinkTarget.Route("GetOrderById", new { id = o.Id }));
        builder.Link("customer", o => LinkTarget.Route("GetCustomer", new { id = o.CustomerId }));
    }
}
```

Ketting navigates by relation. `go()` addresses a resource (no request yet), `get()` fetches its state, and `follow()` hops a relation:

```javascript
import { Client } from 'ketting';

const client = new Client('https://api.example.com/');

const order = client.go('/orders/42');
const state = await order.get();        // negotiated as HAL-FORMS automatically

console.log(state.data.status);         // your DTO's own properties, untouched

const customer = await order.follow('customer');
const customerState = await customer.get();
```

`follow()` calls chain without intermediate awaits — `client.go('/').follow('orders').follow('item')` — and Ketting caches each resource's state, so re-`get()`ing a resource it has already seen makes no request.

### Templated links

A templated Cairn link expands on the Ketting side by passing variables to `follow()`:

```csharp
builder.Link("search", _ => LinkTarget.Uri("/orders{?status,page}", templated: true));
```

```javascript
const results = await order.follow('search', { status: 'open', page: 2 });
```

## Actions: affordances become forms

A Cairn [affordance](affordances-and-forms.md) renders in HAL-FORMS as a `_templates` entry — method, target, content type, and input fields derived from `Accepts<TInput>()`:

```csharp
builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id }))
    .Post()
    .When(o => o.Status == OrderStatus.Pending);

builder.Affordance("ship", o => LinkTarget.Route("ShipOrder", new { id = o.Id }))
    .Post()
    .Accepts<ShipOrderInput>()
    .Title("Ship this order");
```

Ketting surfaces templates as *actions* on the resource state — look one up by name and submit it:

```javascript
const state = await order.get();

const ship = state.action('ship');       // parsed from _templates.ship
await ship.submit({
  carrier: 'UPS',
  trackingNumber: '1Z...',
});
```

The action carries the target URI, HTTP method, content type, and the field list Cairn derived from your input type's data annotations — so a generic form renderer on the client can display prompts, required flags, ranges, and option lists without knowing the .NET type. Two details to line up:

- **The `default` template key.** HAL-FORMS reserves the key `default` for a resource's primary action, and Cairn emits a *sole* template (or one marked [`AsDefault()`](affordances-and-forms.md#the-default-template-asdefault)) under that key rather than its declared name. On the Ketting side, `state.action()` with no argument selects the default action — so a single-action resource is `state.action().submit(...)`, not `state.action('cancel')`.
- **Conditional affordances just work.** `When(...)` and `RequireAuthorization(...)` gates mean the template simply isn't in the document when the transition isn't available. Render buttons from what `state.action(...)` finds, and the UI stays honest — the same [engine-of-application-state](hateoas.md) contract Cairn's own [typed client](client.md) consumes.
- **Regex validation crosses runtimes.** HAL-FORMS `regex` values are your `[RegularExpression]` patterns verbatim, and Ketting validates them with JavaScript's engine — keep patterns to the ECMAScript-compatible subset (see [the note in Affordances & HAL-FORMS](affordances-and-forms.md#property-derivation)).

## Collections: give Ketting `item` links

Ketting's collection model follows the HAL convention: a collection resource exposes its members through the `item` relation, ideally with the members embedded so they arrive pre-fetched. Cairn's [pagination envelopes](pagination.md) emit `self`/`first`/`prev`/`next`/`last` out of the box; add the `item` relation with a `LinkConfig` on the envelope type itself:

```csharp
public sealed class OrderPageLinks : LinkConfig<PagedResource<OrderDto>>
{
    public override void Configure(ILinkBuilder<PagedResource<OrderDto>> builder)
    {
        builder.EmbedMany("item", page => page.Items);
    }
}
```

`EmbedMany` puts each order's full representation (with its own links and templates) under `_embedded.item`. Per HAL semantics an embedded entry also stands in for the link, and Ketting uses embedded resources to **warm its cache** — so iterating the collection and reading each item costs one HTTP request, not N+1. If you'd rather keep pages light, `builder.Links("item", page => page.Items.Select(o => LinkTarget.Route("GetOrderById", new { id = o.Id })))` emits links only, and Ketting fetches items on demand.

Paging is just link-following:

```javascript
let page = client.go('/orders');

while (true) {
  const state = await page.get();
  for (const item of await page.followAll('item')) {
    // each item is a full Resource — its own links and actions are navigable
  }
  if (!state.links.has('next')) break;
  page = await page.follow('next');
}
```

## React bindings

[react-ketting](https://github.com/badgateway/react-ketting) turns the same resources into hooks. `useResource` binds a component to a resource's state; `useCollection` follows the `item` relation the envelope config above provides:

```jsx
import { KettingProvider, useResource, useCollection } from 'react-ketting';

const App = () => (
  <KettingProvider client={client}>
    <OrderView />
  </KettingProvider>
);

function OrderView() {
  const { loading, error, data, resourceState } = useResource('/orders/42');
  if (loading) return <Spinner />;

  const canCancel = !!resourceState.action('cancel');
  return (
    <>
      <h1>Order {data.id} — {data.status}</h1>
      {canCancel && <button onClick={() => resourceState.action('cancel').submit({})}>Cancel</button>}
    </>
  );
}
```

Components watching the same URI share Ketting's cache and re-render together after a `submit()` marks the resource stale — the client-side counterpart of Cairn recomputing links after a state transition.

## The rest of the round trip

- **Problem details.** Cairn's [error responses](error-responses.md) are RFC 9457 `application/problem+json`, which Ketting parses natively — a failed request surfaces as an error carrying the problem document, not an opaque status code.
- **The `Link` header.** Ketting also reads RFC 8288 `Link` response headers into `state.links`. Enable [`EmitLinkHeader`](formats.md#the-link-header-rfc-8288) and the top-level resource's links survive even on responses a client only inspects headers-first.
- **ETags & preconditions.** [`WithETag`](conditional-requests.md) endpoints emit validators and enforce preconditions (`304`/`412`/`428`). Ketting keeps each state's response headers (`state.headers`), so a client can read the `ETag` there, replay it as `If-Match` on a write, and treat a `412 Precondition Failed` as a concurrency conflict.
- **CORS, for browser apps.** Ketting negotiates and navigates via headers, so a cross-origin API should expose the ones it reads: `Access-Control-Expose-Headers: Link, Location, ETag`. The Ketting wiki has a [full set of suggested CORS headers](https://github.com/badgateway/ketting/wiki/CORS-header-suggestions).
- **CURIEs.** Custom relations documented via [`AddCurie`](embedded-resources.md#curies) appear in `_links.curies` per the HAL spec; prefixed relations like `acme:widget` are followable by their full key.

## Quick reference: Cairn concept → Ketting API

| Cairn (server) | On the wire (HAL-FORMS) | Ketting (client) |
| --- | --- | --- |
| `builder.Self(...)` / `builder.Link(rel, ...)` | `_links` | `resource.follow(rel)`, `state.links` |
| `LinkTarget.Uri(..., templated: true)` | `"templated": true` | `follow(rel, { vars })` |
| `builder.Affordance(name, ...)` | `_templates[name]` | `state.action(name).submit(values)` |
| sole template / `AsDefault()` | `_templates.default` | `state.action()` |
| `Accepts<TInput>()` field derivation | `properties` | action fields (prompts, required, options) |
| `Embed` / `EmbedMany` | `_embedded` | followable + pre-warmed cache |
| `PagedResource<T>` / `CursorPage<T>` | `next`/`prev`/`first`/`last` links | `follow('next')`, `useCollection` |
| [Problem details](error-responses.md) | `application/problem+json` | parsed error objects |
| [`WithETag`](conditional-requests.md) | `ETag` / `412` | `state.headers`, `If-Match` on writes |

## See also

- [Wire formats & negotiation](formats.md) — how the HAL-FORMS answer is chosen.
- [Affordances & HAL-FORMS](affordances-and-forms.md) — everything `_templates` can carry.
- [The typed client](client.md) — Cairn's own .NET client, the C# counterpart to Ketting.
- [Ketting wiki](https://github.com/badgateway/ketting/wiki) — the client's full documentation.
