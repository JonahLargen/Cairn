# Link URL policy

By default, links resolve to absolute URLs built from the incoming request's scheme and host. That is right for the common case — but behind a proxy or gateway whose forwarded headers you can't fix, it leaks internal hostnames into responses, and in some deployments you simply want path-relative links. `CairnOptions` gives you three dials.

## Path-relative links: `UrlStyle`

```csharp
builder.Services.AddCairn(o => o.UrlStyle = LinkUrlStyle.PathRelative);
```

`LinkUrlStyle` has two members: `Absolute` (the default) and `PathRelative`. With `PathRelative`, every link is emitted without scheme or host — `"/orders/1"` instead of `"https://internal-host/orders/1"` — which makes responses immune to host misconfiguration entirely.

## Pinning the public origin: `PublicBaseUri`

When clients need absolute URLs but the request's origin can't be trusted, pin the origin Cairn builds them from:

```csharp
builder.Services.AddCairn(o => o.PublicBaseUri = new Uri("https://api.example.com"));
```

Every absolute link is now rooted at `https://api.example.com` regardless of what host the request arrived on. A path in the base URI becomes the links' path base — `new Uri("https://api.example.com/v2/")` yields links like `https://api.example.com/v2/orders/1`. The URI must be absolute (a relative one throws `ArgumentException`), and the setting is ignored when `UrlStyle` is `PathRelative`.

Both `UrlStyle` and `PublicBaseUri` apply to route-resolved links, affordances, **and** [pagination](pagination.md) links.

## Post-processing: `TransformUrl`

`TransformUrl` runs after resolution on each route-resolved link and affordance URL:

```csharp
builder.Services.AddCairn(o => o.TransformUrl = (http, url) =>
    http.Request.Query.TryGetValue("api-version", out var v) && v.Count > 0
        ? QueryHelpers.AddQueryString(url, "api-version", v.ToString())
        : url);
```

Its main use is carrying request state onto links — the query-string API version above is the canonical example (see [versioning.md](versioning.md)). It does not run on pagination links, which already derive from the request URL and keep its query string.

## Related

- [API versioning](versioning.md) — `TransformUrl` for query-string versioning.
- [Pagination](pagination.md) — how pagination links are built.
