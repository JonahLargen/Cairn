# Testing

`Cairn.Testing` provides assertions for verifying the hypermedia in your responses from integration tests. Parse a response body into a `HypermediaResponse`, then make fluent `.Should()` assertions over its links and affordances. The assertions are built on AwesomeAssertions, so failures produce the same readable messages as the rest of your test suite.

## Parsing a response

`HypermediaResponse.Parse(string json)` reads `_links`, `_actions`, and HAL-FORMS `_templates` from a resource body into two dictionaries: `Links` (keyed by relation) and `Affordances` (keyed by name). Each `_links` entry becomes a `HypermediaLink(string Href, string? Title)`; each `_actions` or `_templates` entry becomes a `HypermediaAffordance(string Href, string Method, string? Title)`.

Two extension methods cover the common entry points:

```csharp
using Cairn.Testing;

// From a JSON string:
HypermediaResponse hypermedia = json.Hypermedia();

// From an HttpResponseMessage:
HypermediaResponse hypermedia = await response.ReadHypermediaAsync();
```

`ReadHypermediaAsync` accepts an optional `CancellationToken`.

## Asserting links and affordances

Call `.Should()` on a `HypermediaResponse` to begin a chain:

| Assertion | Checks |
| --- | --- |
| `HaveSelfLink()` | A `self` link is present |
| `HaveLink(string relation)` | A link with the given relation is present |
| `HaveLink(string relation, string href)` | The link is present and its `Href` equals `href` |
| `NotHaveLink(string relation)` | No link with the given relation is present |
| `HaveAffordance(string name)` | The named affordance is present; returns affordance assertions |
| `NotHaveAffordance(string name)` | No affordance with the given name is present |

`HaveAffordance(string name)` returns a `HypermediaAffordanceAssertions` exposing `WithMethod(HttpMethod method)` and `WithHref(string href)`. Both return the affordance assertions, so they chain directly; use the `And` property to return to the response-level chain.

## Example: integration test

This test boots an in-memory server with `WebApplicationFactory<T>`, requests a resource, and asserts both a `self` link and an `update` affordance.

```csharp
using System.Net.Http;
using AwesomeAssertions;
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

        HttpResponseMessage response = await client.GetAsync("/orders/42");
        response.EnsureSuccessStatusCode();

        HypermediaResponse hypermedia = await response.ReadHypermediaAsync();

        hypermedia.Should()
            .HaveSelfLink()
            .And.HaveLink("self", "/orders/42")
            .And.HaveAffordance("update")
                .WithMethod(HttpMethod.Put)
                .WithHref("/orders/42")
            .And.NotHaveAffordance("delete");
    }
}
```

`WithMethod` compares the affordance's parsed `Method` string against `HttpMethod.Method`, so the method must match exactly (for example, `PUT`).

## See also

- [The typed client](client.md) for consuming hypermedia in application code.
- [Affordances & HAL-FORMS](affordances-and-forms.md) for the shape of the `_actions` and `_templates` being asserted.
