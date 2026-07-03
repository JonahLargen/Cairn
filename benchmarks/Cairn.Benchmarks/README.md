# Cairn.Benchmarks

Measures what Cairn costs per response, end to end, and how that cost decomposes.

```bash
dotnet run -c Release --project benchmarks/Cairn.Benchmarks -- --filter '*'
```

## The suites

**`EndToEndBenchmarks`** — a paged collection served through an in-process TestHost, at a
representative page size (**50 items**) and a stress size (**1000 items**), five ways:

| Variant | What it isolates |
| --- | --- |
| `page, no links` (baseline) | The floor: the same payload with no hypermedia at all. |
| `page, hand-rolled links` | The real-world alternative: `_links`/`_actions` objects declared on the DTO and built inline in the handler. This is the comparison that matters — nobody chooses between links and no links; they choose between Cairn computing links and writing them by hand. |
| `page, WithLinks + route configs` | The full pipeline with `LinkTarget.Route(...)`: every item href goes through ASP.NET Core's `LinkGenerator` (route-value binding + URL generation), which dominates the per-item cost. |
| `page, WithLinks + explicit-URI configs` | The same pipeline with `LinkTarget.Uri(...)`: the escape hatch for hot collection endpoints, skipping `LinkGenerator` entirely. |
| `page, WithLinks, unconfigured items` | The cost of doing nothing: the filter attached but the item type unconfigured — endpoint filter, compute-stage walk, and per-object emit-stage lookups with no links produced. |

**`SingleResourceBenchmarks`** — the per-request floor on a single-resource endpoint, the most
representative shape of real API traffic.

**`SerializerOverheadBenchmarks`** — the serializer-level tax of the injected hypermedia
properties, with no request in flight. This tax applies to **every** JSON response in the host
app (the contract modifier lives in the global `JsonOptions`), so it is measured on the cheapest
possible path and the smallest possible DTO — it is a floor and a worst-case ratio at once.

## How to read the results

**Prefer marginal per-item cost over the ratio column.** The baseline endpoint does almost
nothing, so ratios against it look dramatic while the absolute numbers stay small. The number
that predicts production behavior is:

```
(linked mean − baseline mean) / page size        → µs per linked item
(linked alloc − baseline alloc) / page size      → bytes per linked item
```

Judge that against what the endpoint's handler already does (auth, a database query measured in
milliseconds) and the request rate. As a worked example: ~2 µs and ~2 KB per item means a
50-item page pays ~100 µs of CPU and ~100 KB of short-lived Gen0 allocation — invisible next to
a 5 ms database call, but worth knowing at four digits of RPS.

**The decision-relevant comparison is against `hand-rolled links`, not against `no links`.**
Cairn's honest price is its premium over building the same link objects by hand — both make the
payload bigger and both allocate per item.

**Route configs pay for `LinkGenerator`; explicit-URI configs don't.** If a hot endpoint serves
large pages with per-item route links and the compute cost shows up in profiles, switching that
config to `LinkTarget.Uri` interpolation removes the dominant term. That trade (hand-maintained
paths, no route-table validation) already exists in the library — the benchmark exists to
quantify it.

## Methodology notes

- Responses are **drained to `Stream.Null`** (via `ResponseHeadersRead` + a pooled copy buffer)
  rather than buffered into a `byte[]`. A linked payload is legitimately several times larger
  than the plain one; buffering it client-side would charge the size difference — including
  large-object-heap churn above 85 KB — to the Allocated/Gen columns, conflating "the response
  carries more data" with the pipeline cost being measured.
- Everything runs through **TestHost**, in process. That removes network noise but also
  Kestrel's pooled response pipes, so allocation numbers skew high relative to a real server for
  anything proportional to body size.
- 50 items approximates a typical API page; 1000 items is a deliberate stress case, not a claim
  about real workloads.

## Deliberate non-goals

Two numbers in these results are known, bounded, and intentionally not optimization targets:

- **The single-resource delta** (single-digit microseconds per request: filter, negotiation,
  scope setup, one link computation). It is below the noise floor of any real request.
- **The serializer floor** (double-digit *nanoseconds* per serialized object for the injected
  property checks). The ratio in `SerializerOverheadBenchmarks` looks large only because the
  benchmark DTO is two ints — the smallest object that can carry the tax.

Re-litigating these is a waste of a profiling session; the numbers are kept here so regressions
are visible, not because they need to shrink.
