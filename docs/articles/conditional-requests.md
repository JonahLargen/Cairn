# Conditional requests, OPTIONS & deprecation

Hypermedia tells a client *what* it can do next; the HTTP layer around it — validators, preconditions, `Allow`, deprecation headers — tells it *how* to do so safely. Cairn's [typed client](client.md) already sends `If-None-Match`/`If-Match`; this page is the server side of that round trip.

## ETags on reads: `WithETag`

`WithETag` derives an entity tag from the response value, sets the `ETag` header, and answers a matching `If-None-Match` on `GET`/`HEAD` with **304 Not Modified**:

```csharp
app.MapGet("/orders/{id:int}", (int id, IOrderRepo repo) => TypedResults.Ok(repo.Get(id)))
   .WithLinks()
   .WithETag((OrderDto o) => o.Version);
```

The delegate receives the endpoint's value (result unions and `TypedResults.Ok(...)` are unwrapped first). The returned tag is quoted automatically unless it already is (`"v1"` and `W/"v1"` pass through), and `If-None-Match` comparison is weak, per RFC 9110.

A conditional `GET` that short-circuits to 304 is a *healthy* outcome: Cairn recognizes it and does not count the response's computed hypermedia as [never emitted](diagnostics.md).

`WithETag` also leaves endpoint metadata the [OpenAPI integrations](openapi.md) pick up, documenting an `ETag` response header on each success response and a `304 Not Modified` response — so a generated client knows to send `If-None-Match` and handle the 304.

## Preconditions on writes: `CairnPreconditions.Evaluate`

For updates, evaluate the request's conditional headers against the resource's current tag before applying the change:

```csharp
app.MapPut("/orders/{id:int}", (int id, OrderDto dto, HttpRequest req, IOrderRepo repo) =>
    CairnPreconditions.Evaluate(req, repo.Get(id)?.Version, requireIfMatch: true)
        ?? Results.NoContent());
```

Pass `null` for the tag when the resource has no current representation (it doesn't exist yet). `Evaluate` checks both write preconditions per RFC 9110 §13:

- **`If-Match`** — `null` when a listed tag strongly matches the current one (weak tags never match a write precondition), or `*` and the resource exists; otherwise **412 Precondition Failed** (as `application/problem+json`) — the client's copy is stale, or the resource is gone.
- **`If-None-Match`** — **412** when a listed tag weakly matches, or `*` and the resource exists. This is what makes `PUT ... If-None-Match: *` the standard create-only request: it succeeds only when nothing is there to overwrite.
- With no conditional header at all, `null` — unless `requireIfMatch: true`, in which case **428 Precondition Required** forces clients to send one. A create guarded by `If-None-Match: *` satisfies the requirement.

## Answering OPTIONS: `UseCairnOptionsHandler`

```csharp
app.UseCairnOptionsHandler();
```

The middleware answers `OPTIONS` requests with **204 No Content** and an `Allow` header built from every endpoint whose route pattern matches the path:

```
OPTIONS /orders/1
→ 204, Allow: GET, HEAD, PUT, OPTIONS
```

`HEAD` is included whenever `GET` is, `OPTIONS` always is, and methods are listed in conventional order. An endpoint your app maps for `OPTIONS` itself (or an any-method endpoint) wins over the middleware, and unmatched paths fall through to your 404. Unlike the deprecation headers below, this middleware is not auto-registered — add the `UseCairnOptionsHandler()` call yourself.

CORS preflights (OPTIONS requests carrying `Access-Control-Request-Method`) are always passed through untouched, so `UseCors` can answer them with the `Access-Control-*` headers browsers require — the relative order of the two middlewares doesn't matter for preflights. Note that the handler answers without any authorization check (no endpoint is matched, so endpoint authorization never runs); if advertising a path's methods to anonymous callers is a concern, map OPTIONS explicitly on those routes instead.

## Deprecating endpoints: `WithDeprecation`

```csharp
app.MapGet("/old-orders", () => TypedResults.Ok(legacy.List()))
   .WithDeprecation(
       deprecatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
       sunset: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
       link: "https://docs.example.com/deprecations/old-orders");
```

emits the standard deprecation headers on every response from the endpoint:

- `Deprecation` — RFC 9745; the structured-field date `@<unix-seconds>`. Prefer supplying `deprecatedAt` — when omitted, the registration time (app startup) is used, which changes on every deployment. (The literal `true` existed only in the draft and is invalid under the final RFC.)
- `Sunset` — RFC 8594 HTTP date, when `sunset` is given.
- `Link: <url>; rel="deprecation"` — when `link` is given.

`WithDeprecation` declares endpoint metadata rather than an endpoint filter; the headers are emitted by a middleware `AddCairn` auto-registers at the front of the pipeline. That is what lets it work on MVC controller endpoints too — `app.MapControllers().WithDeprecation(...)` deprecates the whole group. Because the middleware comes from `AddCairn`, calling `WithDeprecation` without `AddCairn` would be a silent no-op — Cairn logs a once-per-host warning if it detects that.

The same metadata drives the [OpenAPI integrations](openapi.md): a `WithDeprecation` endpoint is marked `deprecated: true` in the document, so client generators and Swagger UI flag it — no header inspection required.

## Related

- [The typed client](client.md) — the consuming side: `ifNoneMatch`, `ifMatch`, and 304 handling.
- [Diagnostics & observability](diagnostics.md) — how 304s interact with the unemitted-hypermedia diagnostic.
- [Error responses](error-responses.md) — the `application/problem+json` shape 412/428 responses use.
