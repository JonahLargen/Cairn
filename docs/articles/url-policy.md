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

> [!NOTE]
> With `Absolute` URLs, the request's `Host` header is what links are built from — and it is client-controlled and proxy-rewritten. When neither `ForwardedHeadersOptions` (via the options system) nor `PublicBaseUri` is configured, Cairn logs a one-time warning at startup. Configure forwarded headers, pin `PublicBaseUri`, or switch to `PathRelative` to silence it.

## Per-request origins (multi-tenant): `ResolvePublicBaseUri`

`PublicBaseUri` pins one origin for the whole app. When a single app serves several origins — a tenant per host, a regional edge per request — resolve the origin from the request instead:

```csharp
builder.Services.AddCairn(o => o.ResolvePublicBaseUri = http =>
    tenants.TryGetOrigin(http.Request.Host.Host, out var origin) ? origin : null);
```

The resolver takes precedence over `PublicBaseUri`: a non-null `Uri` becomes this request's origin, and returning `null` falls back to `PublicBaseUri` (then the incoming request's own scheme and host), so a resolver can rebase only the tenants it recognizes. The URI it returns must be absolute — a relative one throws at request time. Keep it cheap and side-effect-free; it may be consulted several times while a response's links are built. Like `PublicBaseUri`, it feeds route links, affordances, and pagination links alike, and is ignored under `PathRelative`. Configuring it also silences the startup `Host`-header warning, since the host now controls the origin.

## Post-processing: `TransformUrl`

`TransformUrl` runs after resolution on **every** emitted URL — route-resolved links, affordances, explicit `LinkTarget.Uri` hrefs, and [pagination](pagination.md) links:

```csharp
builder.Services.AddCairn(o => o.TransformUrl = (http, url) =>
    http.Request.Query.TryGetValue("api-version", out var v) && v.Count > 0
        ? QueryHelpers.AddQueryString(url, "api-version", v.ToString())
        : url);
```

Its main use is carrying request state onto links — the query-string API version above is the canonical example (see [versioning.md](versioning.md)) — but it also fits URL signing, tracking parameters, or CDN rewrites that should reach the whole document. Because pagination links already derive from the request URL (query string included), write the transform to be idempotent so a parameter that's already present isn't appended twice.

## Related

- [API versioning](versioning.md) — `TransformUrl` for query-string versioning.
- [Pagination](pagination.md) — how pagination links are built.
