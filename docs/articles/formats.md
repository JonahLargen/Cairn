# Wire formats & content negotiation

Cairn projects a resource's `_links`, affordances, and embedded resources into one of three wire formats. The format controls the JSON shape and the `Content-Type` the response carries. It is resolved per request, so the same handler can answer with Cairn's default shape, HAL, or HAL-FORMS depending on what the caller asks for.

## The three formats

`HypermediaFormat` has three members:

```csharp
public enum HypermediaFormat
{
    Default,
    Hal,
    HalForms,
}
```

| Format | Media type | Emits |
| --- | --- | --- |
| `HypermediaFormat.Default` | `application/json` | `_links` and `_actions` |
| `HypermediaFormat.Hal` | `application/hal+json` | `_links` only — affordances are not emitted |
| `HypermediaFormat.HalForms` | `application/prs.hal-forms+json` | `_links` and `_templates` for affordances |

`_links` is always present when a resource has links. The difference is how affordances surface:

- **Default** writes affordances as an `_actions` object.
- **HAL-FORMS** writes affordances as `_templates`.
- **HAL** has no notion of write actions, so affordances are dropped. When a resource carrying affordances is served as HAL, Cairn logs a warning naming the resource type and suggesting HAL-FORMS.

See [affordances-and-forms.md](affordances-and-forms.md) for how `_actions` and `_templates` are built, and [embedded-resources.md](embedded-resources.md) for `_embedded` and CURIEs, which are emitted in every format.

## Resolution precedence

For each response the format is chosen in this order; the first match wins.

1. **Per-endpoint override** — `.WithHypermediaFormat(...)` on the endpoint or route group. This forces the format, bypassing negotiation and the default.
2. **`Accept` negotiation** — when `CairnOptions.NegotiateFormat` is `true` (the default) and the request's `Accept` header names a known hypermedia media type.
3. **`CairnOptions.DefaultFormat`** — used when nothing above applies (default `HypermediaFormat.Default`).

### Forcing a format per endpoint

```csharp
app.MapGet("/orders/{id}", GetOrder)
    .WithLinks()
    .WithHypermediaFormat(HypermediaFormat.HalForms);
```

```csharp
var v2 = app.MapGroup("/v2").WithLinks();
v2.WithHypermediaFormat(HypermediaFormat.Hal);
```

`.WithHypermediaFormat` is an `IEndpointConventionBuilder` extension, so it applies to a single endpoint or to a whole route group.

### Accept negotiation

When no per-endpoint format is set, Cairn inspects the request's `Accept` header. Quality values are honored per RFC 9110: a media type with `q=0` is excluded, and among the acceptable types the highest quality wins. The recognized media types are:

| `Accept` media type | Selected format |
| --- | --- |
| `application/prs.hal-forms+json` | `HypermediaFormat.HalForms` |
| `application/hal+json` | `HypermediaFormat.Hal` |
| `application/json`, `application/*+json`, `application/*`, `*/*` | `HypermediaFormat.Default` |

A higher-quality plain `application/json` (or wildcard) outranks a lower-quality `hal`/`hal-forms`. For example:

```
Accept: application/hal+json;q=0.5, application/json;q=0.9
```

selects `HypermediaFormat.Default`, because the plain JSON type has the higher quality. Unrecognized media types are ignored. If the `Accept` header is absent or names nothing recognizable, negotiation defers to `CairnOptions.DefaultFormat`.

Set `NegotiateFormat = false` to disable `Accept`-based selection entirely, so the format comes only from a per-endpoint override or `DefaultFormat`:

```csharp
builder.Services.AddCairn(options =>
{
    options.NegotiateFormat = false;
    options.DefaultFormat = HypermediaFormat.Hal;
});
```

## Content-Type relabeling

When a top-level resource is served as HAL or HAL-FORMS, Cairn relabels the response's `Content-Type` to match the format:

- `HypermediaFormat.Hal` → `application/hal+json`
- `HypermediaFormat.HalForms` → `application/prs.hal-forms+json`
- `HypermediaFormat.Default` leaves the content type as is.

Relabeling is conservative. It only rewrites a content type whose media type is exactly `application/json`; the media type is swapped and **every parameter is preserved**. So a media-type API version like `application/json; v=2` becomes `application/hal+json; v=2`, and a `charset` is carried through unchanged. See [versioning.md](versioning.md) for media-type versioning.

Content types that are not plain `application/json` are left untouched — including `application/problem+json` (per RFC 9457; see [error-responses.md](error-responses.md)) and any explicit vendor media type the handler already set. Relabeling also applies only when the top-level returned value is itself a decorated resource (a configured single resource, or an offset/cursor [pagination](pagination.md) envelope). A bare collection serializes as a JSON array — its elements still carry `_links`, but the array itself is not a HAL document, so it remains `application/json`.

## One format per response

The resolved format is computed once per request, then used to shape every resource in the response — the top-level value, each element of a returned collection, and every embedded child are all rendered in the same format.

For getting endpoints producing hypermedia in the first place, see [getting-started.md](getting-started.md) and [link-configs.md](link-configs.md).
