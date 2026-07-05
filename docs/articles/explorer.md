# Browsable API explorer

`Cairn.AspNetCore.Explorer` serves a **HAL Explorer** — an in-browser console for navigating your API the way a hypermedia client does. You open it, start at the API's entry point, and move through the API by *following links* and *running actions*, never hand-typing a URL. It is the HATEOAS counterpart to a Swagger UI page: where Swagger lists every operation from a static document, the explorer walks the live graph your responses describe.

Because Cairn already emits HAL `_links`, `_embedded`, and HAL-FORMS `_templates`, the explorer is a pure client of that output — adding it changes nothing about how your resources serialize.

## Install and mount

```bash
dotnet add package Cairn.AspNetCore.Explorer
```

```csharp
var app = builder.Build();

app.UseCairnExplorer();   // served at /explorer

app.MapControllers();
// ... your endpoints
app.Run();
```

Browse to `/explorer` and the console opens on the API root (`/`). That is the entire setup — the UI is a single HTML document embedded in the package, so there is no static-asset middleware to configure and **no external (CDN) request** is ever made.

## What it renders

For whatever resource is loaded, the explorer shows:

- **The response body** — the raw JSON, syntax-highlighted, with every `href` clickable.
- **Links** — each `_links` relation as a row you can follow with one click. Templated links (RFC 6570) prompt for their variables before navigating; link arrays and CURIEs are handled.
- **Embedded** — `_embedded` children listed and drillable, each carrying its own state.
- **Forms** — every HAL-FORMS `_templates` entry rendered as a real form. Field `type`, `required`, `min`/`max`, `placeholder`, `regex`, and inline `options` map to native inputs; submitting sends the template's method and `contentType`. A `201` response follows its `Location`; a `204` reloads the resource so a state change (a cancelled order losing its `cancel` action) is visible immediately.
- **Status** — the status code, content type, round-trip time, and the `Location`/`ETag` response headers.

The explorer requests HAL-FORMS by default so actions render as forms, and its **Accept** selector lets you switch formats live — pick `application/hal+json` to watch the write actions drop away, or the flat `application/vnd.cairn+json` shape to see `_actions`. The media types offered are read from your [`CairnOptions.MediaTypes`](formats.md), so a customized token set is reflected automatically.

Requests are issued on the same origin with the caller's credentials, so the links and actions shown are exactly the ones the current user is [authorized](link-configs.md#requireauthorization) to see.

## Development-only by default

The explorer exposes the whole API surface interactively, so it is served **only in the `Development` environment** unless you say otherwise. Outside development the pipeline is left untouched and `/explorer` resolves as it otherwise would (typically a 404).

To serve it elsewhere, set `Enabled` explicitly — and guard it behind authentication when you do:

```csharp
app.UseCairnExplorer(options => options.Enabled = true);   // served in every environment
```

Setting `Enabled = false` disables it unconditionally, including in development.

## Options

```csharp
app.UseCairnExplorer(options =>
{
    options.Path = "/hal";                 // mount point (default "/explorer")
    options.EntryPoint = "/api";           // the resource opened first (default "/")
    options.Title = "Store API Explorer";  // masthead / tab title
    options.Enabled = null;                // null = Development only (the default); true/false to force
});
```

| Option | Default | Purpose |
| --- | --- | --- |
| `Path` | `/explorer` | The absolute path the UI is served from. Both the exact path and its trailing-slash form answer. |
| `EntryPoint` | `/` | The resource URL the console loads first — usually the API's home/entry-point resource. |
| `Title` | `Cairn HAL Explorer` | The title shown in the masthead and browser tab. |
| `Enabled` | `null` | `null` serves it in `Development` only; `true` serves it everywhere; `false` disables it. |

## Give it something to explore

An explorer is only as good as the graph it can walk. An API that rewards browsing has an **entry-point resource** that links onward, cross-links between related resources, and affordances that advertise what can be done next. The [sample API](https://github.com/JonahLargen/Cairn/tree/main/samples/Cairn.Sample.Api) is built this way — a root that links to the orders and customers collections, orders that cross-link to their customer, and `create`/`cancel` actions backed by an in-memory store — so running it and opening `/explorer` is the quickest way to see the whole picture move.

See also [Affordances & HAL-FORMS](affordances-and-forms.md) for how the forms are described, [Embedded resources](embedded-resources.md) for `_embedded` and CURIEs, and [Wire formats](formats.md) for the negotiation the Accept selector drives.
