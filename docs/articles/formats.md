# Wire formats & content negotiation

Cairn projects a resource's `_links`, affordances, and embedded resources into one of three built-in wire formats — or into a [custom format](custom-formats.md) you register. The format controls the JSON shape and the `Content-Type` the response carries. It is resolved per request, so the same handler can answer with Cairn's default shape, HAL, or HAL-FORMS depending on what the caller asks for.

## The three built-in formats

`HypermediaFormat` has three wire-format members, plus `None` (no hypermedia at all — see [opt-in links](#opt-in-links-only-when-the-client-asks)):

```csharp
public enum HypermediaFormat
{
    Default,
    Hal,
    HalForms,
    None,
}
```

| Format | Media type | Emits |
| --- | --- | --- |
| `HypermediaFormat.Default` | `application/json` (also `application/vnd.cairn+json`) | `_links` and `_actions` |
| `HypermediaFormat.Hal` | `application/hal+json` | `_links` only — affordances are not emitted |
| `HypermediaFormat.HalForms` | `application/prs.hal-forms+json` | `_links` and `_templates` for affordances |
| `HypermediaFormat.None` | `application/json` | nothing — the resource serializes exactly as its DTO declares |

The flat `Default` shape answers to two media types: plain `application/json`, and the explicit `application/vnd.cairn+json`. The alias matters when links are made [opt-in](#opt-in-links-only-when-the-client-asks), where `application/json` is reserved for the bare resource.

`_links` is always present when a resource has links. The difference is how affordances surface:

- **Default** writes affordances as an `_actions` object.
- **HAL-FORMS** writes affordances as `_templates`.
- **HAL** has no notion of write actions, so affordances are dropped. When a resource carrying affordances is served as HAL, Cairn logs a warning naming the resource type and suggesting HAL-FORMS.

See [affordances-and-forms.md](affordances-and-forms.md) for how `_actions` and `_templates` are built, and [embedded-resources.md](embedded-resources.md) for `_embedded` and CURIEs, which are emitted in every format. Beyond the built-ins, a registered `IHypermediaFormatter` adds its own media type and JSON shape — see [custom-formats.md](custom-formats.md).

## Resolution precedence

For each response the format is chosen in this order; the first match wins.

1. **Per-endpoint override** — `.WithHypermediaFormat(...)` on the endpoint or route group. This forces the format, bypassing negotiation and the default.
2. **`Accept` negotiation** — when `CairnOptions.NegotiateFormat` is `true` (the default) and the request's `Accept` header names a known hypermedia media type (built-in or a registered custom formatter's).
3. **`CairnOptions.DefaultFormat`** — used when nothing above applies (default `HypermediaFormat.Default`).

When negotiation is enabled, Cairn also appends `Vary: Accept` to negotiable responses, so shared caches key on the header that changes the body shape — leave it intact if you post-process response headers.

### Forcing a format per endpoint

```csharp
app.MapGet("/orders/{id}", GetOrder)
    .WithLinks()
    .WithHypermediaFormat(HypermediaFormat.HalForms);
```

A second overload takes a media type string to force a registered [custom formatter](custom-formats.md): `.WithHypermediaFormat("application/vnd.siren+json")`. Forcing a media type no formatter registered throws an `InvalidOperationException` at request time.

```csharp
var v2 = app.MapGroup("/v2").WithLinks();
v2.WithHypermediaFormat(HypermediaFormat.Hal);
```

`.WithHypermediaFormat` is an `IEndpointConventionBuilder` extension, so it applies to a single endpoint or to a whole route group.

### Accept negotiation

When no per-endpoint format is set, Cairn inspects the request's `Accept` header and negotiates per RFC 9110 §12.5.1. For each format the server can emit, the *most specific* matching range determines its quality — exact media type, then `application/*+json`, then `application/*`, then `*/*` — a `q=0` match excludes the format, and the highest-quality survivor wins. Quality ties break on specificity (so `Accept: */*, application/hal+json` asks for HAL, regardless of order), then on server preference: the configured `DefaultFormat` first, then registered custom formatters, then the remaining built-ins. The emittable media types are:

| Media type | Format |
| --- | --- |
| `application/prs.hal-forms+json` | `HypermediaFormat.HalForms` |
| `application/hal+json` | `HypermediaFormat.Hal` |
| a registered custom formatter's media type | that formatter |
| `application/json` | `HypermediaFormat.Default` |

Explicit `application/json` selects the Default format even when `DefaultFormat` is set to something else. A bare wildcard (`*/*` or `application/*`) matches every format equally, so it expresses no preference and the tie goes to `DefaultFormat` (with `DefaultFormat = Hal`, an `Accept: */*` request gets HAL). `application/*+json` is narrower: it covers the `+json` hypermedia types but **not** plain `application/json`, so it negotiates a hypermedia format even when `DefaultFormat` is Default.

A higher-quality plain `application/json` (or wildcard) outranks a lower-quality `hal`/`hal-forms`. For example:

```
Accept: application/hal+json;q=0.5, application/json;q=0.9
```

selects `HypermediaFormat.Default`, because the plain JSON type has the higher quality — specificity only breaks quality ties. Unrecognized media types are ignored. If the `Accept` header is absent, names nothing emittable, or excludes everything with `q=0`, negotiation defers to `CairnOptions.DefaultFormat` — except that a `q=0` aimed at the default format itself is honored when the header accepts something else (e.g. `Accept: application/hal+json;q=0, */*` with `DefaultFormat = Hal` serves plain JSON, not HAL).

Set `NegotiateFormat = false` to disable `Accept`-based selection entirely, so the format comes only from a per-endpoint override or `DefaultFormat`:

```csharp
builder.Services.AddCairn(options =>
{
    options.NegotiateFormat = false;
    options.DefaultFormat = HypermediaFormat.Hal;
});
```

## Opt-in links: only when the client asks

`.WithLinks()` is a *server-side, per-endpoint* opt-in: the developer chooses which endpoints carry hypermedia. But once an endpoint is opted in, it emits links for **every** request — a plain `application/json` caller gets `_links`/`_actions` whether it wants them or not. There is deliberately no media type that means "the resource, with no links": all four negotiable media types above carry hypermedia, differing only in shape.

Sometimes you want the *client* to decide per request — lean `application/json` for callers that just want data, links for callers that ask. Set `DefaultFormat = HypermediaFormat.None` to flip the un-negotiated default from "flat links-and-actions" to "no hypermedia":

```csharp
builder.Services.AddCairn(options =>
{
    options.AddLinks(new OrderLinks());
    options.DefaultFormat = HypermediaFormat.None;   // hypermedia is opt-in by the client
});
```

With `None` as the default, on an opted-in endpoint:

| Request `Accept` | Response |
| --- | --- |
| `application/json` | the **bare** resource — no `_links`, no `_actions` |
| `*/*`, `application/*`, or no `Accept` header | the bare resource (a wildcard expresses no hypermedia preference) |
| `application/vnd.cairn+json` | Cairn's flat shape, with `_links` and `_actions` |
| `application/hal+json` | HAL, with `_links` |
| `application/prs.hal-forms+json` | HAL-FORMS, with `_links` and `_templates` |
| a registered custom formatter's media type | that format |

So a hypermedia media type is the client's "I understand hypermedia" signal — a dumb client gets clean JSON, a hypermedia-aware client names the shape it wants and gets the links.

### Reaching the flat shape: `application/vnd.cairn+json`

Cairn's flat `_links`/`_actions` shape normally lives at `application/json`. Under opt-in, `application/json` is claimed for the bare resource — so the flat shape gets its own media type, **`application/vnd.cairn+json`**, and that is how a client asks for it:

```bash
curl https://api.example.com/orders/42                                  # bare resource
curl -H "Accept: application/vnd.cairn+json" https://api.example.com/orders/42   # flat _links + _actions
curl -H "Accept: application/hal+json"        https://api.example.com/orders/42   # HAL
```

The response's `Content-Type` echoes `application/vnd.cairn+json` when that shape is negotiated, so caches (already keyed by the `Vary: Accept` Cairn adds) and clients can tell the bare and linked representations apart from the header alone. `application/vnd.cairn+json` is recognized in every mode — even with the default `HypermediaFormat.Default`, a client can name it to request the flat shape explicitly — and it can be forced per endpoint with `.WithHypermediaFormat("application/vnd.cairn+json")`.

`None` is non-breaking: `DefaultFormat` is `HypermediaFormat.Default` unless you change it, so existing apps keep emitting links on `application/json`. A per-endpoint `.WithHypermediaFormat(...)` override still wins over the negotiated default in either mode.

You can also force `None` on a single endpoint or route group to suppress links there even while the app default emits them:

```csharp
app.MapGet("/orders/{id}", GetOrder)
    .WithLinks()
    .WithHypermediaFormat(HypermediaFormat.None);   // this endpoint never emits hypermedia
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

## Hypermedia keys are emitted verbatim

The keys inside `_links`, `_actions`, `_templates`, and `_embedded` — relation names, action names, CURIE prefixes — are protocol identifiers, not data. They are emitted exactly as declared, even when the host configures a `JsonSerializerOptions.DictionaryKeyPolicy` (snake case, upper case, …) that renames ordinary dictionary keys.

For getting endpoints producing hypermedia in the first place, see [getting-started.md](getting-started.md) and [link-configs.md](link-configs.md).
