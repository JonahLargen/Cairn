# Caching hypermedia responses

Hypermedia makes response bodies *vary* in ways plain JSON doesn't: the same URL can serve different shapes to different `Accept` headers (format negotiation) and different link sets to different callers (policy-gated links, `When(...)` conditions over per-user state). Both interact with caching, and ASP.NET Core's two server-side caches behave very differently.

## Two caches, two rules

| | Honors the response `Vary` header? | What to do |
| --- | --- | --- |
| HTTP caches: CDNs, proxies, `ResponseCaching` middleware | **Yes** | Nothing — Cairn already emits `Vary: Accept` on negotiable responses |
| `OutputCache` middleware | **No** | Configure the policy yourself (below) |

Cairn appends `Vary: Accept` to every response where format negotiation is enabled (`CairnOptions.NegotiateFormat`, on by default), per RFC 9110 §12.5.5. That is enough for any cache that speaks HTTP — a CDN will store separate variants for `application/hal+json` and `application/json` clients.

## OutputCache ignores `Vary`

ASP.NET Core's [output caching](https://learn.microsoft.com/aspnet/core/performance/caching/output) middleware does **not** read the response's `Vary` header. It splits cache entries only by what its own policy declares. On an output-cached endpoint with format negotiation, whichever format is requested first gets cached and replayed to every client, whatever they ask for.

Tell the policy to vary by `Accept`:

```csharp
builder.Services.AddOutputCache(options =>
    options.AddBasePolicy(policy => policy.SetVaryByHeader("Accept")));

// or per endpoint:
app.MapGet("/orders", GetOrders)
    .WithLinks()
    .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(30)).SetVaryByHeader("Accept"));
```

Alternatively, disable negotiation (`o.NegotiateFormat = false`) or force one format per endpoint (`.WithHypermediaFormat(...)`) so the body no longer depends on `Accept`.

## Policy-gated links must not be shared-cached

Links and affordances gated with `RequireAuthorization(...)` — and any `When(...)` condition that reads the current user — make the *body* caller-dependent:

```csharp
builder.Affordance("cancel", o => LinkTarget.Route("CancelOrder", new { id = o.Id }))
    .RequireAuthorization("CanCancel");
```

An output-cached response computed for an admin will replay the `cancel` affordance to every anonymous caller (and vice versa: a response cached for an anonymous caller hides actions from admins). The links are only *advertisements* — the endpoint itself still enforces its authorization — but the document is wrong, and it leaks which actions exist.

Cairn detects this combination at request time: when a response whose link config references authorization policies is computed while output caching is active for the request (`IOutputCacheFeature` present), it logs a warning once per resource type.

Options, in order of preference:

1. **Don't output-cache** endpoints whose configs gate hypermedia on policies. Output caching is for hot, identical-for-everyone responses; per-caller documents aren't that.
2. **Vary by caller** with a custom policy (`VaryByValue` on a user/role key). This keeps correctness but multiplies cache entries — and note the built-in middleware refuses to cache authenticated requests by default for exactly this class of reason; think twice before overriding that.
3. **Move the gating out of the document** — emit the link for everyone and let the endpoint reject unauthorized calls. Only acceptable when revealing the action's existence is fine.

The same reasoning applies to any shared HTTP cache: a response with per-caller links should be `Cache-Control: private` (or `no-store`) so a CDN never stores it. Cairn doesn't set cache-control headers for you — that stays an explicit host decision.

## Related

- [Wire formats & negotiation](formats.md) — what `Accept` changes about the body.
- [Affordances & HAL-FORMS](affordances-and-forms.md) — `RequireAuthorization` on links and affordances.
- [Conditional requests, OPTIONS & deprecation](conditional-requests.md) — ETags and `304 Not Modified` for hypermedia responses.
