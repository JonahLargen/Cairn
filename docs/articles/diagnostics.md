# Diagnostics & observability

Hypermedia failures are easy to miss: a link that doesn't resolve, a response shape that can't carry links, a type nothing configured. Cairn's position is that these should never fail *silently* — in the default `Lax` mode a problem costs you a link, not a 500, but every drop is logged and metered so it can be seen.

This page covers the runtime side. The build-time counterpart — analyzers that catch unknown route names and unconfigured types before you run — is [route safety](route-safety.md).

## Logged warnings

All warnings log under the category `Cairn.AspNetCore` at `Warning` level, and each fires **once per condition per host** — a 1,000-item page or a chatty test suite produces one line, not thousands. (The gate is a per-host singleton, so side-by-side hosts such as `WebApplicationFactory` suites each get their own warnings.)

| Warning | When it fires |
| --- | --- |
| Unresolved link | A link or affordance failed to resolve in `Lax` mode (for example an unknown route name) and was dropped. Once per resource type + relation. |
| Affordances lost to HAL | The negotiated format is HAL, which has no affordance section, and the resource declared affordances. |
| Value-type resource | The endpoint returned a `struct`. Cairn correlates hypermedia to instances by reference; a value type boxes to a different instance at each stage, so links can't attach. Use a class or record. |
| `IAsyncEnumerable<T>` response | An async stream can't be enumerated twice, so it can't be linked. Materialize first (`ToListAsync()`). |
| Unconfigured type | An endpoint opted in with `.WithLinks()`/`[CairnLinks]` but the returned type (and no base type) has a registered `LinkConfig<T>`. Also caught at build time by [CAIRN002](route-safety.md#cairn002-withlinks-endpoint-with-no-linkconfig). |
| Deferred sequence in an immutable result | The response is a deferred sequence (`IQueryable`, LINQ projection) inside `TypedResults.Ok(...)`, where Cairn cannot buffer it: computing links enumerates it once and serialization enumerates it again (an `IQueryable` runs its query twice). Fires while the request runs. Materialize (`ToList()`) before wrapping. |
| Deferred envelope items without a settable property | A pagination envelope exposes deferred items but no settable property to buffer them back into (the property is init-only or computed), so the sequence is enumerated twice and item links may be lost. |
| Custom `JsonConverter` on a resource type | Hypermedia was computed for a type whose JSON contract is handled by a custom converter, so the property injection can never emit it. |
| Computed but never emitted | Hypermedia was computed for instances that never reached the serializer — typically a deferred sequence whose re-enumeration produced fresh instances. Materialize (`ToList()`) before wrapping. |

In `Strict` mode (`o.Mode = LinkResolutionMode.Strict`) an unresolved link throws a `LinkResolutionException` instead of dropping — see [getting started](getting-started.md).

If you want to react to drops programmatically (fail a canary, page a dashboard), `LinkContext.OnUnresolvedLink` is the underlying hook: it receives an `UnresolvedLink(Type, LinkRelation, LinkTarget)` for every Lax-mode drop.

## Tracing and metrics

Cairn publishes an `ActivitySource` and a `Meter`, both named `Cairn.AspNetCore`. The names are available as constants so you never hardcode them:

```csharp
using Cairn.AspNetCore;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(CairnDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(CairnDiagnostics.MeterName));
```

**Tracing** — one `Cairn.ComputeHypermedia` span per linked top-level value, tagged with `cairn.format` (the negotiated format name or custom media type) and `cairn.resource_type`. It measures the compute stage: evaluating conditions and policies and resolving URLs, not serialization.

**Metrics** — all counters:

| Instrument | Unit | Meaning |
| --- | --- | --- |
| `cairn.resources.linked` | `{resource}` | Resources that received hypermedia. |
| `cairn.links.computed` | `{link}` | Links computed. |
| `cairn.affordances.computed` | `{affordance}` | Affordances computed. |
| `cairn.links.unresolved` | `{link}` | Lax-mode drops, tagged `cairn.resource_type` and `cairn.relation`. The metric counts every drop even though the log line fires once. |
| `cairn.hypermedia.unemitted` | `{resource_type}` | Responses whose computed hypermedia never reached the wire, tagged `cairn.resource_type`. |

`cairn.links.unresolved` and `cairn.hypermedia.unemitted` are the two to alert on: both mean clients are receiving less hypermedia than the code declares.

## Related

- [Route safety](route-safety.md) — the build-time analyzers (CAIRN001–CAIRN003).
- [Getting started](getting-started.md) — `Lax` vs `Strict` resolution modes.
- [Link configurations](link-configs.md) — how types resolve to configs (base-class fallback).
