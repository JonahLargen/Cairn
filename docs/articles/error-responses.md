# Error responses

Cairn decorates a response based on what it serializes, not on its HTTP status code. An error body is hypermedia only when it is something Cairn understands.

## When an error body is decorated

The compute stage inspects the returned value and only relabels the response media type when the top-level value is itself a decorated resource object — a configured single resource, or a paged/cursor envelope. Two consequences follow for error responses:

- An `application/problem+json` document keeps its content type. Cairn never rewrites a problem body to `application/hal+json` or `application/prs.hal-forms+json`.
- A body Cairn has no link configuration for keeps its content type as well; an uncovered value is left untouched.

The content-type swap is conservative on the wire: it only ever rewrites `application/json` to the negotiated hypermedia type, leaving vendor types and `problem+json` alone. So a problem document — whether produced by `Results.Problem(...)`, a `ProblemDetails` body, or any explicit `application/problem+json` response — passes through unchanged.

To make an error carry links and actions, return a problem built to do so.

## Hypermedia-bearing problems

`CairnResults.Problem` creates an RFC 9457 problem document that can carry hypermedia. `HypermediaProblem` is an `IResult`, so a minimal-API endpoint can return it directly.

```csharp
public static HypermediaProblem Problem(
    int status,
    string? title = null,
    string? detail = null,
    string? type = null,
    string? instance = null)
```

The standard members map directly to the RFC 9457 fields:

- `Status` — the HTTP status code, also written as the `status` member.
- `Type` — a URI identifying the problem type (`type`).
- `Title` — a short, human-readable summary (`title`).
- `Detail` — a human-readable explanation specific to this occurrence (`detail`).
- `Instance` — a URI identifying the specific occurrence (`instance`).

`Type`, `Title`, `Detail`, and `Instance` are written only when non-null. The document always writes the `status` member, and it always serializes as `application/problem+json`.

### Adding links, actions, and extensions

`HypermediaProblem` exposes three fluent builders, each returning the same instance:

```csharp
public HypermediaProblem WithLink(string relation, string href, string? title = null)
public HypermediaProblem WithLink(string relation, LinkTarget target, string? title = null)
public HypermediaProblem WithAction(string name, string href, string method = "POST")
public HypermediaProblem WithAction(string name, LinkTarget target, string method = "POST")
public HypermediaProblem WithExtension(string name, object? value)
```

- `WithLink` adds an entry to the problem's `_links`. Each entry has an `href`, plus a `title` when supplied. Repeated calls for the same relation emit a HAL link array under that relation (a single link stays an object), and relations differing only in case group into one entry under the first-declared casing, per RFC 8288 — the same rules as the [main formatter](formats.md). Keys are emitted verbatim, unaffected by any JSON dictionary-key policy.
- `WithAction` adds an affordance to the problem's `_actions` — for example a `retry` the client can invoke. `method` defaults to `POST`.
- Both accept a `LinkTarget` instead of a raw href — `LinkTarget.Route("GetOrder", new { id })` resolves through the host's [URL policy](url-policy.md) (`UrlStyle`, `PublicBaseUri`) exactly like a link config's targets, so problem links survive route restructuring too. An unresolvable target is dropped in `Lax` mode and throws `LinkResolutionException` in `Strict`.
- `WithExtension` adds a problem extension member, written as a top-level field alongside the standard members. Reserved member names (`type`, `title`, `status`, `detail`, `instance`, `_links`, `_actions`) are rejected — set those through the dedicated properties and methods.

When the host registers the problem-details pipeline (`builder.Services.AddProblemDetails(...)`), `HypermediaProblem` writes through `IProblemDetailsService`, so `CustomizeProblemDetails` and any custom `IProblemDetailsWriter` apply to it like to any framework-produced problem — the links and actions ride along as extension members. Without `AddProblemDetails` (or when the registered writer declines the request), the document is written directly, unchanged from previous behavior.

A complete example:

```csharp
app.MapPost("/orders/{id:int}/checkout", (int id) =>
{
    if (!InventoryAvailable(id))
    {
        return CairnResults.Problem(
            status: StatusCodes.Status409Conflict,
            title: "Out of stock",
            detail: "One or more items are no longer available.",
            type: "https://example.com/problems/out-of-stock",
            instance: $"/orders/{id}")
            .WithExtension("orderId", id)
            .WithLink("self", $"/orders/{id}", "The order")
            .WithLink("describedby", "https://example.com/problems/out-of-stock")
            .WithAction("retry", $"/orders/{id}/checkout", "POST");
    }

    return Results.Ok(/* ... */);
});
```

The response is `application/problem+json` with the standard members plus `_links` and `_actions`:

```json
{
  "type": "https://example.com/problems/out-of-stock",
  "title": "Out of stock",
  "status": 409,
  "detail": "One or more items are no longer available.",
  "instance": "/orders/42",
  "orderId": 42,
  "_links": {
    "self": { "href": "/orders/42", "title": "The order" },
    "describedby": { "href": "https://example.com/problems/out-of-stock" }
  },
  "_actions": {
    "retry": { "href": "/orders/42/checkout", "method": "POST" }
  }
}
```

The members are written in this order: `type`, `title`, `status`, `detail`, `instance`, then any extension members in the order they were added, then `_links`, then `_actions`. `_links` and `_actions` are written only when at least one entry has been added.

Because the body is `application/problem+json`, an error can still tell the client what to do next while remaining a valid RFC 9457 document for clients that ignore the hypermedia.

## Consuming problems

The typed client surfaces a returned problem as a `Problem` (RFC 9457). See [client.md](client.md) for `ClientResult` / `ClientResult<T>` and the `IsSuccess`, `Problem`, and `EnsureSuccess` members. For how successful responses are decorated and negotiated, see [formats.md](formats.md).
