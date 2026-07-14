# Pagination

Cairn generates standard navigation links for paged responses — `self`, `first`, `prev`, `next`, and `last` for offset pages, and `self`, `prev`, `next` for cursor pages. The links are derived from the page metadata you return and the current request URL, so endpoints stay declarative.

Each item on the page is still linked by its own type's [link configuration](link-configs.md). Pagination links describe how to navigate between pages; item links describe each item.

## Offset pagination

Return a `PagedResource<T>` from an endpoint that opts in with `.WithLinks()`. It carries `Items` (an `IReadOnlyList<T>`), `Page` (1-based), `PageSize`, and `TotalCount`; `TotalPages` is computed.

```csharp
app.MapGet("/widgets", (int page = 1, int pageSize = 20) =>
{
    var total = WidgetStore.Count;
    IReadOnlyList<Widget> items = WidgetStore.Page(page, pageSize);
    return new PagedResource<Widget>(items, page, pageSize, total);
})
.WithLinks();
```

Cairn projects the navigation links onto the response:

```json
{
  "_links": {
    "self":  { "href": "https://api.example.com/widgets?page=2" },
    "first": { "href": "https://api.example.com/widgets?page=1" },
    "prev":  { "href": "https://api.example.com/widgets?page=1" },
    "next":  { "href": "https://api.example.com/widgets?page=3" },
    "last":  { "href": "https://api.example.com/widgets?page=5" }
  }
}
```

`first` and `last` appear when `TotalPages > 0`. `prev` appears when `Page > 1` and is clamped to `TotalPages`, so an over-range request (`Page > TotalPages`) still points back into range. `next` appears when `Page < TotalPages`.

### Binding the query parameters

The handler above redeclares knowledge Cairn already has: the names of the paging parameters, their defaults, and their bounds. Opt in to binding them by declaring a `PageRequest` parameter instead — it binds from the same query parameters the pagination links swap, so the values you page by can never drift from the URLs Cairn writes into `_links`:

```csharp
app.MapGet("/widgets", (PageRequest paging, Store store) =>
{
    IReadOnlyList<Widget> items = store.Widgets.Skip(paging.Skip).Take(paging.PageSize).ToList();
    return paging.ToResource(items, store.Widgets.Count);
})
.WithLinks();
```

`Page` binds from `PageQueryParameter` (default `page`, absent = 1) and `PageSize` from `PageSizeQueryParameter` (default `pageSize`, absent = `DefaultPageSize`, which defaults to 20). `Skip` is the precomputed offset `(Page - 1) * PageSize`, and `ToResource` wraps the fetched page in a `PagedResource<T>` stamped with the bound values, so the envelope — and the links derived from it — describe exactly the page the client asked for.

Set `MaxPageSize` to stop a client from dictating an unbounded fetch; a size above the cap is clamped to it (and the envelope echoes the clamped size, so the metadata stays truthful):

```csharp
cairn.DefaultPageSize = 25;
cairn.MaxPageSize = 100;
```

A repeated, non-integer, or non-positive value is the client's error: the request is rejected with `400 Bad Request` (the framework's standard rejection for a required parameter that failed to bind; the precise reason is logged). Declare the parameter non-nullable so that rejection applies.

In a controller action, call `PageRequest.Bind(HttpContext)` — same rules, same configured names — which throws a descriptive `BadHttpRequestException` on a malformed value:

```csharp
[HttpGet]
public PagedResource<Widget> Get()
{
    var paging = PageRequest.Bind(HttpContext);
    return paging.ToResource(_store.Page(paging.Page, paging.PageSize), _store.Count);
}
```

The OpenAPI/Swagger integrations document the bound query parameters on the operation — under the configured names, with the default and cap — because ApiExplorer cannot see into the binding on its own (this covers minimal-API endpoints; a controller action calling `Bind` declares no bindable parameter to describe). A query parameter the endpoint documents itself under the same name wins.

### Without implementing the interface

If your response is an existing envelope type you would rather not change, register a reader with `AddPaging<T>` instead. It maps your type to a `PagedView` (`Items`, `Page`, `PageSize`, `TotalCount`); the type gets pagination links without implementing `IPagedResource`. Like [link configurations](link-configs.md), the registration honors inheritance: a subclass of a registered envelope type is decorated the same way.

```csharp
builder.Services.AddCairn(cairn =>
{
    cairn.AddLinksFromAssemblyContaining<Program>();
    cairn.AddPaging<WidgetEnvelope>(e =>
        new PagedView(e.Data, e.PageNumber, e.PageSize, e.Total));
});
```

### Customizing the page URL

By default the offset links swap the `page` query parameter on the current request URL, preserving any other query parameters. Matching is case-insensitive and keeps the incoming casing: a request with `?Page=2` gets links with `Page=3`, not a duplicate `page` parameter. Change the parameter name globally with `PageQueryParameter`:

```csharp
cairn.PageQueryParameter = "p";
```

To control the whole URL, set the app-wide `PageLink`, a `Func<HttpRequest, int, string>` that builds the URL for a page number:

```csharp
cairn.PageLink = (request, page) =>
    $"{request.Scheme}://{request.Host}/widgets/page/{page}";
```

Override it per route or route group with `.WithPageLinks(...)`:

```csharp
app.MapGet("/widgets", Handler)
    .WithLinks()
    .WithPageLinks((request, page) =>
        $"{request.Scheme}://{request.Host}/widgets/page/{page}");
```

## Cursor pagination

For keyset/cursor navigation, return a `CursorPage<T>`. It carries `Items` (an `IReadOnlyList<T>`) and the opaque `Next` and `Prev` cursors (each `null` when there is no such page). The cursors are not serialized into the body — they surface only as links.

```csharp
app.MapGet("/events", (string? cursor = null, int limit = 50) =>
{
    var (items, next, prev) = EventStore.Read(cursor, limit);
    return new CursorPage<Event>(items, Next: next, Prev: prev);
})
.WithLinks();
```

```json
{
  "_links": {
    "self": { "href": "https://api.example.com/events?cursor=abc" },
    "next": { "href": "https://api.example.com/events?cursor=def" },
    "prev": { "href": "https://api.example.com/events?cursor=aaa" }
  }
}
```

`self` is the current request URL. `next` and `prev` appear only when the corresponding cursor is non-empty.

### Binding the cursor parameters

Like the offset shape, the cursor and limit can be bound instead of redeclared — a `CursorRequest` parameter reads the same query parameter the cursor links swap:

```csharp
app.MapGet("/events", (CursorRequest paging, EventStore store) =>
{
    var (items, next, prev) = store.Read(paging.Cursor, paging.Limit);
    return new CursorPage<Event>(items, Next: next, Prev: prev);
})
.WithLinks();
```

`Cursor` binds from `CursorQueryParameter` (default `cursor`; absent or empty means `null` — the first page) and `Limit` from `LimitQueryParameter` (default `limit`), with the same `DefaultPageSize`/`MaxPageSize` defaults, cap, and 400-on-malformed rules as `PageRequest`. In a controller action, call `CursorRequest.Bind(HttpContext)`. The OpenAPI/Swagger integrations document both query parameters on the operation.

### Without implementing the interface

Register a reader with `AddCursorPaging<T>` to map an existing envelope to a `CursorView` (`Items`, `Next`, `Prev`) without implementing `ICursorPagedResource`:

```csharp
cairn.AddCursorPaging<EventEnvelope>(e =>
    new CursorView(e.Data, e.NextCursor, e.PrevCursor));
```

### Customizing the cursor URL

By default the cursor links swap the `cursor` query parameter on the current request URL. Change the parameter name globally with `CursorQueryParameter`:

```csharp
cairn.CursorQueryParameter = "after";
```

Set the app-wide `CursorLink`, a `Func<HttpRequest, string, string>` that builds the URL for a cursor:

```csharp
cairn.CursorLink = (request, cursor) =>
    $"{request.Scheme}://{request.Host}/events?token={cursor}";
```

Override it per route or route group with `.WithCursorLinks(...)`:

```csharp
app.MapGet("/events", Handler)
    .WithLinks()
    .WithCursorLinks((request, cursor) =>
        $"{request.Scheme}://{request.Host}/events?token={cursor}");
```

## Item links

Pagination links are added alongside the page's items — they do not replace per-item links. Every element of the returned page is linked according to its own runtime type's [link configuration](link-configs.md), and renders into the active [wire format](formats.md). On the [typed client](client.md), follow `next`/`prev`/`first`/`last` with `FollowAsync` to page through results.

## Details that keep envelopes safe

- **Deferred `Items` are materialized once.** An envelope carrying an unmaterialized sequence (an `IQueryable`, a LINQ projection) is buffered before serialization, so the underlying query runs once and the per-item links survive re-enumeration.
- **URL policy applies.** Pagination links honor `CairnOptions.UrlStyle`, `PublicBaseUri`, and the per-request `ResolvePublicBaseUri` like every other link — see [url-policy.md](url-policy.md). They also pass through `TransformUrl`; because they already derive from the request URL (query string included), write that transform to be idempotent so an already-present parameter isn't appended twice.
- **Documents stay honest.** The OpenAPI/Swagger integrations describe pagination envelopes — including types adapted via `AddPaging`/`AddCursorPaging` — with their navigation `_links` and negotiable media types; see [openapi.md](openapi.md).
