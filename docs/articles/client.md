# The typed client

`Cairn.Client` is a small, allocation-light client for consuming Cairn hypermedia APIs. It reads a resource's typed body together with its links, affordances, and embedded resources, then lets you navigate by relation and invoke actions by name — without hand-building URLs.

The client never throws on an HTTP error status. Every request returns a result object that is either a success (carrying a `Resource<T>`) or a failure (carrying a [`Problem`](error-responses.md)). You opt in to exceptions with `EnsureSuccess()`.

## Registration

`AddCairnClient` registers `CairnClient` as a typed `HttpClient` over `IHttpClientFactory`, so it participates in DI, message handlers, and resilience. It returns the `IHttpClientBuilder` for further configuration.

```csharp
using Cairn.Client;

builder.Services.AddCairnClient(o =>
{
    o.BaseAddress = new Uri("https://api.example.com");
    o.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    o.AllowLink = uri => uri.Host == "api.example.com";
});
```

`CairnClientOptions` exposes:

- `BaseAddress` — the `Uri?` requests are made against; required to resolve relative links.
- `JsonOptions` — the `JsonSerializerOptions?` used to deserialize resource bodies (defaults to `JsonSerializerDefaults.Web`).
- `AllowLink` — a `Func<Uri, bool>?` policy gating which absolute link targets may be followed (see [SSRF protection](#ssrf-protection)).

Because the return value is an `IHttpClientBuilder`, you can chain handlers and resilience:

```csharp
builder.Services
    .AddCairnClient(o => o.BaseAddress = new Uri("https://api.example.com"))
    .AddStandardResilienceHandler();
```

Inject `CairnClient` wherever you need it. To construct one directly — for example in a test — pass an `HttpClient`:

```csharp
var client = new CairnClient(httpClient, jsonOptions: null, allowLink: null);
```

Construction is non-invasive: the injected `HttpClient`'s default headers are never mutated. If the client declares no default `Accept`, Cairn negotiates per request with `application/prs.hal-forms+json`, `application/hal+json; q=0.9`, `application/json; q=0.8`; a caller-declared default `Accept` wins and is left untouched. Caller-supplied `JsonOptions` are used as-is (not copied); when omitted, all clients share one cached `JsonSerializerDefaults.Web` instance.

## Fetching a resource

`GetAsync<T>` reads a resource and its hypermedia, returning a `ClientResult<T>`:

```csharp
ClientResult<Order> result = await client.GetAsync<Order>("/orders/42");

if (result.IsSuccess)
{
    Resource<Order> order = result.Resource;
    Order? value = order.Value;
}
else
{
    Problem problem = result.Problem; // RFC 9457 problem detail
}
```

`ClientResult<T>` members:

- `IsSuccess` — `true` for a 2xx status, or a `304 Not Modified` answer to a conditional request. When `true`, `Resource` is non-null; otherwise `Problem` is non-null.
- `Status` — the HTTP status code as an `int`.
- `IsNotModified` — `true` when the status is `304` (see [Conditional requests](#conditional-requests)).
- `Resource` — the `Resource<T>?` on success.
- `Value` — a shortcut for `Resource?.Value`, otherwise `default`.
- `Problem` — the parsed `Problem?` on failure.
- `EnsureSuccess()` — returns the `Resource<T>` on success, or throws `CairnClientException` on an error status.

`RequireValue()` is on `Resource<T>` — it returns a non-null body or throws `InvalidOperationException` when the body was empty or could not be deserialized:

```csharp
Resource<Order> order = (await client.GetAsync<Order>("/orders/42")).EnsureSuccess();
Order value = order.RequireValue();
```

## Navigating links

A `Resource<T>` exposes its links keyed by relation and lets you follow them without building URLs. `FollowAsync<TNext>` resolves the link for a relation and fetches the next resource:

```csharp
ClientResult<Customer> customer = await order.FollowAsync<Customer>("customer");
```

Other navigation members on `Resource<T>`:

- `Links` — `IReadOnlyDictionary<string, Link>`, the first link per relation.
- `LinksFor(relation)` — `IReadOnlyList<Link>`, all links sharing a relation (a HAL link array).
- `HasLink(relation)` — whether a relation is present.
- `Embedded<TChild>(relation)` — `IReadOnlyList<Resource<TChild>>` parsed from HAL `_embedded` (see [Embedded resources](embedded-resources.md)).

`FollowAsync<TNext>(relation)` throws `InvalidOperationException` if the resource has no link with that relation, so guard with `HasLink` when a relation is optional.

Two curie helpers round out navigation: `Curies` lists the [CURIEs](embedded-resources.md) the response declared (`Curie(Name, Href, Templated)` records from `_links.curies`), and `DocumentationFor(relation)` expands a prefixed relation like `"acme:widget"` against its curie template into the documentation URL (`null` for unprefixed relations or unknown prefixes).

### Templated links

When a link is an RFC 6570 URI template (`Link.Templated` is `true`), pass `variables` — an anonymous object or dictionary — and the client expands the template before fetching:

```csharp
// e.g. a "search" link of /orders{?status,page}
ClientResult<OrderList> page = await resource.FollowAsync<OrderList>(
    "search",
    new { status = "open", page = 2 });
```

Following a templated `Link` without variables throws `NotSupportedException`. You can also follow a `Link` value directly via `client.FollowAsync<T>(link)` or `client.FollowAsync<T>(link, variables)`.

Template expansion implements RFC 6570 levels 1–4 (`{var}`, `{+var}`, `{#var}`, `{.var}`, `{/var}`, `{;var}`, `{?a,b}`, `{&var}`, the prefix modifier `{var:n}`, and explode `{list*}` over lists and maps), with careful encoding: reserved expansion percent-encodes a bare `%` that isn't a valid escape, and the prefix modifier counts code points, so it never splits a surrogate pair.

## Collections

`GetCollectionAsync<TItem>` reads a collection where each item is a navigable resource, returning a `CollectionResult<TItem>`. `itemsProperty` names the array property on an envelope (default `items`); a bare JSON array is read directly.

```csharp
CollectionResult<Order> result = await client.GetCollectionAsync<Order>("/orders");
CollectionResource<Order> orders = result.EnsureSuccess();

foreach (Resource<Order> item in orders.Items)
{
    Order value = item.RequireValue();
}
```

`CollectionResource<TItem>` carries the collection's own hypermedia — `Items`, `Links`, `LinksFor(relation)`, `HasLink`, `Affordances`, `HasAffordance`, `ETag`, `InvokeAsync` — plus `FollowAsync(relation, itemsProperty = "items")` to page to another `CollectionResult<TItem>` of the same item type:

```csharp
if (orders.HasLink("next"))
{
    CollectionResult<Order> next = await orders.FollowAsync("next");
}
```

A second overload, `FollowAsync(relation, variables, itemsProperty = "items")`, handles templated pagination links. A templated `next` is followable even with no variables — unresolved expressions collapse per RFC 6570, so `/orders/2{?verbose}` follows as `/orders/2` — and passing variables expands them. Two guard rails: passing variables for a *non-templated* link throws `ArgumentException`, and calling `FollowAsync("next", null)` throws `ArgumentNullException` explaining the overload trap (a bare `null` binds to `itemsProperty`; write `FollowAsync("next", (object?)null)` to mean "no variables").

`CollectionResult<TItem>` exposes `IsSuccess`, `Status`, `IsNotModified`, `Collection`, `Problem`, and `EnsureSuccess()`. See [Pagination](pagination.md) for the server side that emits `next`/`prev`/`first`/`last`.

## Invoking affordances

Affordances are the available actions a resource advertises (their HTTP method, target, and — under HAL-FORMS — input fields). Invoke one by name on the resource.

`InvokeAsync(name, body?, ifMatch?)` returns a `ClientResult` (no body); `InvokeAsync<TResult>(name, body?, ifMatch?)` reads the returned resource into a `ClientResult<TResult>`:

```csharp
// No returned body
ClientResult ack = await order.InvokeAsync("cancel");
ack.EnsureSuccess();

// With a request body and a returned resource
ClientResult<Order> updated = await order.InvokeAsync<Order>(
    "ship",
    body: new { carrier = "UPS", trackingNumber = "1Z..." });
```

`ClientResult` (the no-body result) exposes `IsSuccess`, `Status`, `Problem`, and `EnsureSuccess()`. Invoking an unknown affordance name throws `InvalidOperationException`; guard with `HasAffordance(name)`.

### Describing inputs

Under [HAL-FORMS](affordances-and-forms.md), an affordance describes the fields it accepts. `Fields(name)` returns the `IReadOnlyList<AffordanceField>` for an affordance (empty if none are described):

```csharp
foreach (AffordanceField field in order.Fields("ship"))
{
    Console.WriteLine($"{field.Name} ({field.Type}) required={field.Required}");
}
```

`AffordanceField` carries `Name`, `Prompt`, `Required`, `ReadOnly`, `Type`, `Value`, `Templated`, `Placeholder`, `Regex`, `MinLength`, `MaxLength`, `Min`, `Max`, `Step`, `Cols`, `Rows`, `Options` (each an `AffordanceFieldOption` with a `Value` and optional `Prompt`), `OptionsLink`, and `SelectedValues` — enough to render or validate a form before invoking.

### Form-aware submission: `SubmitAsync`

Where `InvokeAsync` sends whatever body you give it, `SubmitAsync(name, values, ifMatch?)` (and `SubmitAsync<TResult>`) validates the values against the affordance's HAL-FORMS fields **before anything is sent**:

```csharp
ClientResult<Order> result = await order.SubmitAsync<Order>(
    "update",
    new { reason = "customer request", severity = 3 });
```

Violations — a missing required field, a value set for a read-only field, a regex mismatch (HTML5 whole-value semantics), `maxLength`/`min`/`max` breaches, a value outside the field's `options` — are aggregated into a single `ArgumentException` listing every problem, and the server is never contacted. On success the request goes out with the affordance's method and declared content type.

### Request bodies and content types

For both `InvokeAsync` and `SubmitAsync`, the affordance's declared `contentType` decides how the body is encoded:

- No declared content type, `application/json`, or any `+json` suffix — sent as JSON with the declared media type and parameters preserved (`application/json; charset=utf-8` stays as-is; `application/merge-patch+json` is honored).
- `application/x-www-form-urlencoded` — top-level scalar values are form-encoded (`null` values skipped); a nested object or array throws `NotSupportedException`.
- Anything else — `NotSupportedException`.

## Conditional requests

The client supports ETag-based conditional GETs and optimistic concurrency.

A successful `Resource<T>` exposes its response `ETag`. Pass it back as `ifNoneMatch` to a later `GetAsync` for a conditional read; a `304` surfaces as `IsNotModified`:

```csharp
Resource<Order> order = (await client.GetAsync<Order>("/orders/42")).EnsureSuccess();
string? etag = order.ETag;

ClientResult<Order> again = await client.GetAsync<Order>("/orders/42", ifNoneMatch: etag);
if (again.IsNotModified)
{
    // Unchanged — keep the previously cached resource.
}
```

A 304 is a success, not a failure: `IsSuccess` is `true`, `Problem` is `null`, and `EnsureSuccess()` yields a bodiless resource (`Value` is `null`) whose `ETag` is preserved. The same holds for a 304 answered to an invoked affordance or a collection request. `GetCollectionAsync` takes the same `ifNoneMatch`, and a `CollectionResource<TItem>` exposes its `ETag` to feed back:

```csharp
CollectionResource<Order> orders = (await client.GetCollectionAsync<Order>("/orders")).EnsureSuccess();

CollectionResult<Order> again = await client.GetCollectionAsync<Order>("/orders", ifNoneMatch: orders.ETag);
if (again.IsNotModified)
{
    // Unchanged — keep the previously cached page.
}
```

The server side of this round trip is [`WithETag`](conditional-requests.md).

For writes, pass the same `ETag` as `ifMatch` to an affordance so the server can reject a stale update (`412 Precondition Failed`):

```csharp
ClientResult<Order> result = await order.InvokeAsync<Order>(
    "update",
    body: new { note = "Expedite" },
    ifMatch: order.ETag);

if (!result.IsSuccess && result.Status == 412)
{
    // Concurrency conflict — re-fetch and retry.
}
```

## SSRF protection

Because a client navigates URLs the server supplies, an untrusted API could point a link at an internal host. The `AllowLink` policy gates which absolute targets may be followed or invoked: it is called with the resolved absolute `Uri` and may return `false` to reject it.

```csharp
builder.Services.AddCairnClient(o =>
{
    o.BaseAddress = new Uri("https://api.example.com");
    o.AllowLink = uri => uri.Host == "api.example.com";
});
```

The policy fails closed: if a target cannot be resolved to an absolute URI (no `BaseAddress` set), the link is rejected rather than followed unchecked. A rejected target throws `InvalidOperationException`. When `AllowLink` is `null`, any server-supplied link is followed.

When `AllowLink` is set through `AddCairnClient`, the policy is also enforced on **every redirect hop**: automatic redirect following moves into a Cairn handler that re-checks each 3xx target (up to 10 hops), applies the standard method rewrites (303 → GET; 301/302 from POST → GET; 307/308 preserve the method), strips the `Authorization`, `Cookie`, and `Proxy-Authorization` headers on cross-origin redirects, and hands a body-preserving redirect it can't safely replay back to the caller as the 3xx response.

## Error handling

Every result exposes the parsed `Problem` on failure — an [RFC 9457 problem detail](error-responses.md) with `Type`, `Title`, `Status`, `Detail`, `Instance`, and an `Extensions` dictionary (`IReadOnlyDictionary<string, JsonElement>`) of any non-standard members (such as validation `errors`). When the error body is not `application/problem+json`, the client reads a status-only `Problem`.

```csharp
ClientResult<Order> result = await client.GetAsync<Order>("/orders/42");
if (!result.IsSuccess)
{
    Problem problem = result.Problem;
    if (problem.Extensions.TryGetValue("errors", out var errors))
    {
        // Inspect validation errors.
    }
}
```

A malformed *success* response follows the same no-throw model: a 2xx body that isn't valid JSON, or that can't bind to `T`, returns a failed result whose `Problem` carries the original status, the title "The response body is not valid JSON.", and a detail naming the content type, the parse error, and a short snippet of the body. An empty or whitespace body is not a parse failure — it's a bodiless success.

`EnsureSuccess()` (on `ClientResult`, `ClientResult<T>`, and `CollectionResult<TItem>`) throws `CairnClientException`, which carries the status and the `Problem`, when you prefer exceptions over branching.

## See also

- [Getting started](getting-started.md)
- [Pagination](pagination.md)
- [Embedded resources, link arrays & CURIEs](embedded-resources.md)
- [Affordances & HAL-FORMS](affordances-and-forms.md)
- [Conditional requests, OPTIONS & deprecation](conditional-requests.md)
- [Error responses & problem details](error-responses.md)
- [Packages](packages.md)
