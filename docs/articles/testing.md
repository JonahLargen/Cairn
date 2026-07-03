# Testing

`Cairn.Testing` provides assertions for verifying the hypermedia in your responses from integration tests. Parse a response body into a `HypermediaResponse`, then make fluent `.Should()` assertions over its links, affordances, templates, and embedded resources. The package is assertion-framework-agnostic — it depends on no third-party assertion library; a failed assertion throws `CairnAssertionException` with a message describing both the expectation and the actual hypermedia, which every test framework reports cleanly.

## Parsing a response

Parsing reads `_links`, `_actions`, HAL-FORMS `_templates`, and `_embedded` from a resource body into five collections:

- `Links` — the first link per relation, keyed by relation.
- `AllLinks` — every link per relation in document order, including HAL link arrays and `curies`.
- `Affordances` — keyed by name, populated from `_actions` *and* `_templates`.
- `Templates` — the HAL-FORMS templates as `HypermediaTemplate` values, with their fields.
- `Embedded` — the `_embedded` resources per relation (a single embed parses as a one-element list).

Each link is a `HypermediaLink(string Href, string? Title)` (with `Name`, `Templated`, and the RFC 8288 attributes `Type`, `Deprecation`, `Hreflang`, and `Profile`); each affordance is a `HypermediaAffordance(string Href, string Method, string? Title)` — an action or template that omits its `method` defaults to `GET`, as HAL-FORMS prescribes. A link or action without an `href` — or a template with an empty `target` — fails parsing with a `FormatException`; a template *omitting* `target` falls back to the `self` link.

Four entry points cover the common cases:

```csharp
using Cairn.Testing;

// From a JSON string:
HypermediaResponse hypermedia = HypermediaResponse.Parse(json);

// From an HttpResponseMessage:
HypermediaResponse hypermedia = await response.ReadHypermediaAsync();

// An array-root body (a bare collection): one HypermediaResponse per element.
IReadOnlyList<HypermediaResponse> items = await response.ReadHypermediaListAsync();
// (or HypermediaResponse.ParseAll(json) from a string)

// GET a URL and parse in one step (fails with a clear message on a non-success status):
HypermediaResponse hypermedia = await client.GetHypermediaAsync("/orders/42");
```

`Parse` throws a `FormatException` on an array-root body (pointing at `ParseAll`) rather than silently parsing it as an empty resource, which would let every negative assertion pass. `ReadHypermediaAsync`, `ReadHypermediaListAsync`, and `GetHypermediaAsync` accept an optional `CancellationToken`.

## Asserting links and affordances

Call `.Should()` on a `HypermediaResponse` to begin a chain:

| Assertion | Checks |
| --- | --- |
| `HaveSelfLink()` | A `self` link is present |
| `HaveLink(string relation)` | A link with the given relation is present |
| `HaveLink(string relation, string href)` | The link is present and its `Href` equals `href` |
| `HaveLinkMatching(string relation, string pattern)` | Some link under the relation matches the [pattern](#href-patterns) |
| `HaveTemplatedLink(string relation)` | The link is present with `templated: true` |
| `NotHaveLink(string relation)` | No link with the given relation is present |
| `HaveAffordance(string name)` | The named affordance is present; returns affordance assertions |
| `NotHaveAffordance(string name)` | No affordance with the given name is present |
| `HaveTemplate(string name)` | The named HAL-FORMS template is present; returns template assertions |
| `HaveEmbedded(string relation)` | An `_embedded` entry is present; returns assertions over its first resource |

`HaveAffordance(string name)` returns a `HypermediaAffordanceAssertions` exposing `WithMethod(HttpMethod method)`, `WithHref(string href)`, and `WithHrefMatching(string pattern)`. All return the affordance assertions, so they chain directly; use the `And` property to return to the response-level chain.

`HaveTemplate(string name)` returns `HypermediaTemplateAssertions` — `WithMethod`, `WithTarget`, `WithTargetMatching(pattern)`, `WithContentType`, and `HaveField(name)`, which drills into a single HAL-FORMS field (`ThatIsRequired()`, `ThatIsOptional()`, `ThatIsReadOnly()`, `WithType(...)`, `WithRegex(...)`, `WithPrompt(...)`).

### Href patterns

`HaveLinkMatching`, `WithHrefMatching`, and `WithTargetMatching` accept a lightweight pattern instead of an exact URL, so assertions survive the random host/port of an in-memory test server:

- `{param}` matches exactly one path segment (it never spans a `/`).
- A trailing `*` makes the pattern a prefix match.
- Everything else is literal — `.`, `+`, and friends are not regex metacharacters.

```csharp
hypermedia.Should()
    .HaveLinkMatching("self", "http://{host}/orders/{id}")
    .And.HaveAffordance("cancel").WithHrefMatching("http://{host}/orders/{id}/cancel");
```

## Example: integration test

This test boots an in-memory server with `WebApplicationFactory<T>`, requests a resource, and asserts both a `self` link and an `update` affordance.

```csharp
using System.Net.Http;
using Cairn.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class OrderHypermediaTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OrderHypermediaTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Order_exposes_self_link_and_update_action()
    {
        using HttpClient client = _factory.CreateClient();

        HypermediaResponse hypermedia = await client.GetHypermediaAsync("/orders/42");

        hypermedia.Should()
            .HaveSelfLink()
            .And.HaveLinkMatching("self", "http://{host}/orders/42")
            .And.HaveAffordance("update")
                .WithMethod(HttpMethod.Put)
                .WithHrefMatching("http://{host}/orders/42")
            .And.NotHaveAffordance("delete");
    }
}
```

`WithMethod` compares the affordance's parsed `Method` string against `HttpMethod.Method`, so the method must match exactly (for example, `PUT`).

## Snapshot testing

`HypermediaSnapshot` renders a response as stable, snapshot-friendly JSON: object keys sorted, indentation and newlines normalized, characters unescaped.

```csharp
string snapshot = HypermediaSnapshot.Render(json, new HypermediaSnapshotOptions
{
    HypermediaOnly = true,   // keep only _links/_actions/_templates/_embedded (recursing into _embedded)
    NormalizeHref = href => href.Replace("http://localhost:5123", "<host>"),
});

// or straight from a response:
string snapshot = await HypermediaSnapshot.RenderAsync(response);
```

`HypermediaOnly` strips the resource's own data so the snapshot captures just the hypermedia contract; `NormalizeHref` runs over every `href` and `target` value, which is where volatile hosts and ports live. Feed the result to your snapshot tool of choice (Verify, a checked-in `.txt`, …).

## See also

- [The typed client](client.md) for consuming hypermedia in application code.
- [Affordances & HAL-FORMS](affordances-and-forms.md) for the shape of the `_actions` and `_templates` being asserted.
