# Cairn vs GraphQL vs OData

"Should we use hypermedia, GraphQL, or OData?" is a common question, but the three aren't really rivals — they answer **different questions about the client/server contract**:

- **GraphQL** answers *"how does a client read exactly the data it needs?"* — a typed schema and a query language for composing reads (and mutations) against it.
- **OData** answers *"how does a client query entity sets over HTTP with a standard grammar?"* — uniform `$filter`/`$select`/`$expand` conventions plus machine-readable metadata.
- **Hypermedia (Cairn)** answers *"how does a client know what it can do next?"* — each response carries the links and actions valid for *this resource, in this state, for this caller*.

If your pain is clients hardcoding URLs, re-implementing state machines, and duplicating permission checks, that's the hypermedia question. If your pain is over-fetching and stitching together five endpoints to render one screen, that's the GraphQL question. If your pain is every list endpoint growing ad-hoc `?status=&sortBy=&fields=` parameters, that's the OData question. This page compares the three honestly — including where Cairn is the wrong tool.

## At a glance

| | Cairn (hypermedia REST) | GraphQL | OData |
| --- | --- | --- | --- |
| Contract lives in | each **response** (links & actions) + OpenAPI | the **schema** (introspectable SDL) | the **`$metadata`** document (CSDL) |
| Client reads data by | fetching resources, following links, `_embedded` | composing queries with exact field selection | entity-set URLs + `$filter`, `$select`, `$expand`, `$orderby`, `$top` |
| Client writes by | invoking advertised **actions** (HAL-FORMS) | calling **mutations** from the schema | CRUD on entities + declared actions/functions |
| "Is this operation valid *right now, for me*?" | answered **per response** — invalid actions simply aren't there | not expressible in the schema; client asks or tries | not expressible in `$metadata`¹ |
| Transport | plain HTTP: any verb, any status, cacheable GETs | usually a single `POST /graphql`² | plain HTTP, cacheable GETs |
| HTTP caching / ETags | native (URLs, `Vary`, [`WithETag`](conditional-requests.md)) | client-side normalized caches (Apollo etc.)² | native |
| Typical sweet spot | workflow/state-machine APIs, permission-aware UIs | client-driven read aggregation, BFFs, mobile | data grids, reporting, Excel/Power BI |
| ASP.NET Core libraries | **Cairn** | Hot Chocolate, GraphQL.NET | `Microsoft.AspNetCore.OData` |
| Adoption model | **opt-in per endpoint** | own endpoint & execution engine | per entity-set route convention |

¹ OData's JSON format *can* advertise available actions per instance with `odata.metadata=full` — a hypermedia idea inside OData — but mainstream clients and servers rarely use it.
² GraphQL can serve persisted queries over GET, which restores some HTTP caching; the default single-POST setup gets none.

## What each optimizes for

### GraphQL: client-shaped reads

GraphQL's core win is **letting the client decide the shape of the response**: one round trip returns exactly the fields it selects, across relationships, from a strongly typed schema with first-class tooling (introspection, codegen, GraphiQL). That's the right trade when many differently-shaped clients read the same graph — a mobile app that wants three fields, a dashboard that wants forty — or when a BFF aggregates several backends.

The costs are the mirror image. Every read is a bespoke query the *server* must be able to execute safely (N+1 resolution, depth/complexity limits, per-field authorization), the single-POST transport gives up HTTP caching, conditional requests, and meaningful status codes, and the schema is a **static** contract: it says a `cancelOrder` mutation *exists*, not whether it's valid for order 42 in its current state for the current user. Clients end up re-encoding those rules — exactly the duplication [hypermedia exists to remove](hateoas.md).

### OData: a standard query grammar

OData standardizes what most teams otherwise reinvent ad hoc: filtering, sorting, paging, projection, and expansion as composable query options over entity sets, described by a `$metadata` document generic clients can consume. Its ecosystem is the killer feature — Excel, Power BI, and Power Query can point at an OData feed and just work. If your API is fundamentally *tabular* — entity sets that analysts and grids slice — OData earns its keep.

The trade is that the query surface is a commitment: whatever `$filter` can express, your data layer must execute efficiently (or you curate the allowed options per endpoint). And like GraphQL, the contract is *structural*: `$metadata` describes entities and operations in the abstract, not which transitions are currently available on a given instance.

### Cairn: the server narrates state

Cairn's home turf is resources that are **state machines with rules** — orders, approvals, subscriptions, tickets. The server owns "what can happen next" once, and every response advertises it: a `cancel` action that's present only while the order is `Pending` *and* the caller passes the authorization policy ([affordances](affordances-and-forms.md)), pagination as `next`/`prev` links rather than URL math ([pagination](pagination.md)), forms whose fields a client can render without knowing the .NET type ([HAL-FORMS](affordances-and-forms.md#hal-forms-templates)). Clients shrink to "render what the response offers", URLs become an implementation detail free to change, and it all rides plain HTTP — cacheable GETs, `ETag`s, real status codes, [problem details](error-responses.md).

What Cairn deliberately does **not** give you is ad-hoc querying. There is no `$filter`, no field selection, no client-composed joins — reads are resource-shaped, and the closest analogue to `$expand` is the server *choosing* to inline related resources with [`Embed`/`EmbedMany`](embedded-resources.md). Hypermedia also adds bytes to every opted-in response and a hop to every navigation (embedding mitigates both), and it pays off only if clients actually follow the links — a client that hardcodes URLs against a hypermedia API gets the costs with none of the benefits. Generic hypermedia clients exist ([Ketting](ketting.md) for JavaScript, [Cairn.Client](client.md) for .NET), but they're a smaller ecosystem than GraphQL's or OData's.

## The question that usually decides it

Most comparisons obsess over reads. The sharper differentiator is **writes**: who knows whether an operation is valid *right now*?

| | Where the rule "orders can only be cancelled while pending, by someone with the right permission" lives |
| --- | --- |
| GraphQL / OData | in server-side validation — and **duplicated in every client** that wants to disable the button rather than show an error after the fact |
| Cairn | in one [`LinkConfig<T>`](link-configs.md): `When(o => o.Status == Pending).RequireAuthorization("CanCancelOrders")` — clients check `_actions.cancel` exists |

A schema or metadata document can't answer per-instance, per-caller questions, because it's computed once, not per response. Hypermedia is *only* computed per response — that's the whole idea. Flip it around for reads: a response can't answer "give me just these three fields across four relationships", because that's a per-query question, and per-query is GraphQL's whole idea. Each model is the other's blind spot.

## You don't have to pick one

These compose more often than they compete, and Cairn's [opt-in design](../index.md) is built for it — `.WithLinks()` touches nothing else in the app:

- **Cairn next to OData in one ASP.NET Core app.** Let `Microsoft.AspNetCore.OData` serve the reporting entity sets Power BI reads, and opt the workflow endpoints — the ones with interesting state — into Cairn. Neither is global; they never meet.
- **GraphQL as a BFF over a hypermedia API.** A GraphQL layer aggregates reads for the UI, while its resolvers consume the Cairn API underneath — following links instead of hardcoding backend URLs, so the backend keeps its freedom to restructure routes.
- **Hypermedia only where the client asks.** With [`DefaultFormat = HypermediaFormat.None`](formats.md#opt-in-links-only-when-the-client-asks), plain-JSON consumers (including generated OpenAPI clients) get bare DTOs, and hypermedia-aware clients opt in per request via `Accept`.

## Choosing

Reach for **GraphQL** when many client shapes read one graph, round trips dominate (mobile), or you're building an aggregation layer — and you can invest in resolver performance, query limits, and per-field auth.

Reach for **OData** when the API is entity sets that people slice — admin grids, reporting, anything that should open in Excel or Power BI — and your data layer can honor (a curated subset of) the query options.

Reach for **Cairn** when resources are state machines, UIs must reflect per-user permissions without re-implementing them, or clients should survive URL and workflow changes — and start with just the endpoints where that's true. The fuller version of that judgment call (including when *not* to bother) is in [Do I need this everywhere?](hateoas.md#do-i-need-this-everywhere)

And when the honest answer is "our API is internal, clients are generated from the OpenAPI spec, and nothing has interesting state" — plain JSON is fine. Cairn's opt-in model means choosing it is never all-or-nothing, and neither is declining it.

## See also

- [What is HATEOAS?](hateoas.md) — the concept this library implements.
- [Getting started](getting-started.md) — opt one endpoint in and see it work.
- [Consuming a Cairn API with Ketting](ketting.md) — the generic JavaScript hypermedia client.
- [Wire formats & negotiation](formats.md) — Default, HAL, and HAL-FORMS.
