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

## Preconditions on writes: `CairnPreconditions.Evaluate`

For updates, evaluate the request's `If-Match` against the resource's current tag before applying the change:

```csharp
app.MapPut("/orders/{id:int}", (int id, OrderDto dto, HttpRequest req, IOrderRepo repo) =>
    CairnPreconditions.Evaluate(req, repo.Get(id).Version, requireIfMatch: true)
        ?? Results.NoContent());
```

`Evaluate` returns:

- `null` when `If-Match` strongly matches the current tag (weak tags never match a write precondition) — proceed with the update.
- **412 Precondition Failed** (as `application/problem+json`) when it doesn't match — the client's copy is stale.
- `null` when the header is absent, unless `requireIfMatch: true`, in which case **428 Precondition Required** forces clients to send one.

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

## Deprecating endpoints: `WithDeprecation`

```csharp
app.MapGet("/old-orders", () => TypedResults.Ok(legacy.List()))
   .WithDeprecation(
       deprecatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
       sunset: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
       link: "https://docs.example.com/deprecations/old-orders");
```

emits the standard deprecation headers on every response from the endpoint:

- `Deprecation` — RFC 9745; the structured-field date `@<unix-seconds>` when `deprecatedAt` is given, else the literal `true`.
- `Sunset` — RFC 8594 HTTP date, when `sunset` is given.
- `Link: <url>; rel="deprecation"` — when `link` is given.

`WithDeprecation` declares endpoint metadata rather than an endpoint filter; the headers are emitted by a middleware `AddCairn` auto-registers at the front of the pipeline. That is what lets it work on MVC controller endpoints too — `app.MapControllers().WithDeprecation(...)` deprecates the whole group. Because the middleware comes from `AddCairn`, calling `WithDeprecation` without `AddCairn` would be a silent no-op — Cairn logs a once-per-host warning if it detects that.

## Related

- [The typed client](client.md) — the consuming side: `ifNoneMatch`, `ifMatch`, and 304 handling.
- [Diagnostics & observability](diagnostics.md) — how 304s interact with the unemitted-hypermedia diagnostic.
- [Error responses](error-responses.md) — the `application/problem+json` shape 412/428 responses use.
