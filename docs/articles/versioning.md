# API versioning

Cairn composes with the canonical ASP.NET Core API versioning library (`Asp.Versioning`). Cairn doesn't choose or read a version itself: route links resolve through the standard `LinkGenerator`, and the response content type is relabeled in place. How the version reaches your links depends only on where the versioning scheme puts it.

## URL-segment versioning

When the version lives in a path segment (e.g. `/v1/orders/{id}`), it's part of the route, so it flows into links with no extra configuration. A route-resolved link for a request to `/v1/...` produces `/v1/...`, and a request to `/v2/...` produces `/v2/...`. The ambient version fills route links automatically.

```csharp
public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> b)
    {
        // Resolves against the matched route, including its version segment.
        b.Self(o => LinkTarget.Route("GetOrderById", new { id = o.Id }));
    }
}
```

Nothing about the link configuration changes between versions — the route template carries the segment, and Cairn resolves against the request's matched route.

## Query-string versioning

When the version is a query-string parameter (e.g. `?api-version=1.0`), it isn't part of the route, so route-resolved links won't carry it. Re-apply it with `CairnOptions.TransformUrl`, which post-processes each route-resolved link and affordance URL given the current `HttpContext` and the generated URL:

```csharp
using Microsoft.AspNetCore.WebUtilities;

builder.Services.AddCairn(o => o.TransformUrl = (http, url) =>
    http.Request.Query.TryGetValue("api-version", out var v) && v.Count > 0
        ? QueryHelpers.AddQueryString(url, "api-version", v.ToString())
        : url);
```

`TransformUrl` runs for every route-resolved link and affordance, so all of them stay on the caller's version. Pagination links are not passed through `TransformUrl` because they already preserve the request's other query parameters — the default offset and cursor links swap only the `page` or `cursor` parameter on the current request URL and keep `api-version` (and anything else) intact. See [Pagination: offset & cursor](pagination.md).

## Header and media-type versioning

When the version travels in a request header (e.g. `Api-Version: 1.0`) or in the `Accept` media type (e.g. `application/json; v=1.0`), it's out-of-band relative to the URL by design. Links and affordances stay version-neutral — there's nothing version-specific to put in the URL — and the client re-applies its version on each follow-up request through the same header or media type it already sends.

Media-type version parameters survive Cairn's content-type relabeling. When a response negotiates HAL or HAL-FORMS, Cairn swaps only the media type and keeps the existing parameters — a media-type API version such as `v`, a `charset`, and so on. Relabeling happens only for plain `application/json`; `application/problem+json` and explicit vendor types are left untouched.

```text
Response (Accept: application/json; v=1.0, HAL negotiated):
  Content-Type: application/hal+json; v=1.0
```

The version parameter (`v=1.0`) carries onto the relabeled `application/hal+json` (or `application/prs.hal-forms+json`) content type, so version-aware clients still see their version on the response. For more on negotiation and the relabeled media types, see [Wire formats & negotiation](formats.md).

## Related

- [Link configurations](link-configs.md) — building service-aware link and affordance targets.
- [The typed client](client.md) — following links and re-applying out-of-band versions on the client.
