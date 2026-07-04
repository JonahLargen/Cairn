using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// CollectionResource.EnumerateItemsAsync walks the "next" chain to exhaustion as an IAsyncEnumerable,
// fetching each following page lazily, with optional item/page caps that bound an untrusted server's chain.
public class CairnClientPaginationIteratorTests
{
    [Fact]
    public async Task Walks_next_to_exhaustion_yielding_every_item_in_order()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var seen = new List<int>();
        await foreach (var item in first.EnumerateItemsAsync())
        {
            seen.Add(item.RequireValue().Id);
        }

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, seen);
        Assert.Equal(3, handler.Requests);   // the initial GET plus two "next" hops; nothing beyond exhaustion.
    }

    [Fact]
    public async Task Each_yielded_item_is_a_navigable_resource()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var item = await FirstAsync(first.EnumerateItemsAsync());

        Assert.True(item.HasLink("self"));
        Assert.EndsWith("/items/1", item.Links["self"].Href);
    }

    [Fact]
    public async Task A_single_page_with_no_next_link_yields_only_its_items()
    {
        var handler = new RoutingHandler(new Dictionary<string, string> { ["/only"] = Page([1, 2], "/only", next: null) });
        var first = await FirstPageAsync(handler, "/only");

        var seen = await ToListAsync(first.EnumerateItemsAsync());

        Assert.Equal(new[] { 1, 2 }, seen);
        Assert.Equal(1, handler.Requests);   // no link to follow — no extra request.
    }

    [Fact]
    public async Task An_empty_first_page_yields_nothing()
    {
        var handler = new RoutingHandler(new Dictionary<string, string> { ["/empty"] = Page([], "/empty", next: null) });
        var first = await FirstPageAsync(handler, "/empty");

        Assert.Empty(await ToListAsync(first.EnumerateItemsAsync()));
    }

    [Fact]
    public async Task An_empty_middle_page_does_not_stop_the_walk()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/p1"] = Page([1], "/p1", "/p2"),
            ["/p2"] = Page([], "/p2", "/p3"),   // an empty page in the middle must not end the enumeration.
            ["/p3"] = Page([3], "/p3", next: null),
        });
        var first = await FirstPageAsync(handler, "/p1");

        Assert.Equal(new[] { 1, 3 }, await ToListAsync(first.EnumerateItemsAsync()));
    }

    [Fact]
    public async Task MaxItems_stops_mid_page_without_fetching_the_next_page()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var seen = await ToListAsync(first.EnumerateItemsAsync(maxItems: 1));

        Assert.Equal(new[] { 1 }, seen);       // stopped inside the first page.
        Assert.Equal(1, handler.Requests);     // and never followed "next".
    }

    [Fact]
    public async Task MaxItems_on_a_page_boundary_does_not_fetch_a_further_page()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var seen = await ToListAsync(first.EnumerateItemsAsync(maxItems: 2));

        Assert.Equal(new[] { 1, 2 }, seen);    // the whole first page, exactly at the cap.
        Assert.Equal(1, handler.Requests);     // the cap was met, so no wasted "next" fetch.
    }

    [Fact]
    public async Task MaxItems_spanning_pages_fetches_only_the_pages_it_needs()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var seen = await ToListAsync(first.EnumerateItemsAsync(maxItems: 3));

        Assert.Equal(new[] { 1, 2, 3 }, seen); // all of page 1, then stops mid page 2.
        Assert.Equal(2, handler.Requests);     // page 3 is never fetched.
    }

    [Fact]
    public async Task MaxPages_bounds_the_number_of_pages_read_counting_the_current_page()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var seen = await ToListAsync(first.EnumerateItemsAsync(maxPages: 2));

        Assert.Equal(new[] { 1, 2, 3, 4 }, seen);
        Assert.Equal(2, handler.Requests);     // the in-hand page plus one hop; page 3 is not read.
    }

    [Fact]
    public async Task MaxPages_of_one_yields_only_the_current_page_and_never_follows()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var seen = await ToListAsync(first.EnumerateItemsAsync(maxPages: 1));

        Assert.Equal(new[] { 1, 2 }, seen);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Walks_a_custom_relation()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/c1"] = Page([1], "/c1", "/c2", rel: "successor"),
            ["/c2"] = Page([2], "/c2", next: null, rel: "successor"),
        });
        var first = await FirstPageAsync(handler, "/c1");

        Assert.Equal(new[] { 1, 2 }, await ToListAsync(first.EnumerateItemsAsync(relation: "successor")));
    }

    [Fact]
    public async Task Reads_items_from_a_custom_items_property_on_every_page()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/r1"] = Page([1], "/r1", "/r2", itemsKey: "records"),
            ["/r2"] = Page([2], "/r2", next: null, itemsKey: "records"),
        });
        var first = (await new CairnClient(Client(handler)).GetCollectionAsync<Item>("/r1", itemsProperty: "records")).EnsureSuccess();

        Assert.Equal(new[] { 1, 2 }, await ToListAsync(first.EnumerateItemsAsync(itemsProperty: "records")));
    }

    [Fact]
    public async Task A_failed_page_fetch_mid_walk_throws_after_yielding_the_pages_before_it()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/p1"] = Page([1, 2], "/p1", "/gone"),   // "/gone" is not served — the follow gets a 404.
        });
        var first = await FirstPageAsync(handler, "/p1");

        var seen = new List<int>();
        var exception = await Assert.ThrowsAsync<CairnClientException>(async () =>
        {
            await foreach (var item in first.EnumerateItemsAsync())
            {
                seen.Add(item.RequireValue().Id);
            }
        });

        Assert.Equal(new[] { 1, 2 }, seen);   // the reachable page was fully yielded before the failure surfaced.
        Assert.Equal(404, exception.Status);
    }

    [Fact]
    public async Task Cancellation_stops_the_walk_before_the_next_fetch()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        using var cts = new CancellationTokenSource();
        var seen = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in first.EnumerateItemsAsync().WithCancellation(cts.Token))
            {
                seen.Add(item.RequireValue().Id);
                cts.Cancel();   // cancel while draining page 1 (already in hand).
            }
        });

        Assert.Equal(new[] { 1, 2 }, seen);   // page 1 drained; the page-2 fetch observed the cancellation.
        Assert.Equal(1, handler.Requests);    // only the initial GET ran — the "next" hop never fired.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_max_items_throws_eagerly(int cap)
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        // Eager: an iterator defers its body, so validation must fire on the call, not on first MoveNextAsync.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => first.EnumerateItemsAsync(maxItems: cap));
        Assert.Equal("maxItems", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_max_pages_throws_eagerly(int cap)
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => first.EnumerateItemsAsync(maxPages: cap));
        Assert.Equal("maxPages", exception.ParamName);
    }

    [Fact]
    public async Task A_null_relation_throws_eagerly()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var exception = Assert.Throws<ArgumentNullException>(() => first.EnumerateItemsAsync(relation: null!));
        Assert.Equal("relation", exception.ParamName);
    }

    [Fact]
    public async Task A_null_items_property_throws_eagerly()
    {
        var handler = new RoutingHandler(ThreePageChain());
        var first = await FirstPageAsync(handler, "/p1");

        var exception = Assert.Throws<ArgumentNullException>(() => first.EnumerateItemsAsync(itemsProperty: null!));
        Assert.Equal("itemsProperty", exception.ParamName);
    }

    private static Dictionary<string, string> ThreePageChain() => new()
    {
        ["/p1"] = Page([1, 2], "/p1", "/p2"),
        ["/p2"] = Page([3, 4], "/p2", "/p3"),
        ["/p3"] = Page([5, 6], "/p3", next: null),
    };

    private static async Task<CollectionResource<Item>> FirstPageAsync(RoutingHandler handler, string url)
        => (await new CairnClient(Client(handler)).GetCollectionAsync<Item>(url)).EnsureSuccess();

    private static HttpClient Client(RoutingHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost") };

    // A paged envelope: items (each individually navigable via a "self" link) plus the collection's own
    // "self" and, when present, "next"/custom-relation link.
    private static string Page(int[] ids, string self, string? next, string itemsKey = "items", string rel = "next")
    {
        var items = ids.Select(id => new Dictionary<string, object?>
        {
            ["id"] = id,
            ["_links"] = new Dictionary<string, object?> { ["self"] = new { href = $"/items/{id}" } },
        }).ToArray();

        var links = new Dictionary<string, object?> { ["self"] = new { href = self } };
        if (next is not null)
        {
            links[rel] = new { href = next };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?> { [itemsKey] = items, ["_links"] = links });
    }

    private static async Task<List<int>> ToListAsync(IAsyncEnumerable<Resource<Item>> items)
    {
        var list = new List<int>();
        await foreach (var item in items)
        {
            list.Add(item.RequireValue().Id);
        }

        return list;
    }

    private static async Task<Resource<Item>> FirstAsync(IAsyncEnumerable<Resource<Item>> items)
    {
        await foreach (var item in items)
        {
            return item;
        }

        throw new InvalidOperationException("The sequence was empty.");
    }

    private sealed record Item(int Id);

    private sealed class RoutingHandler(IReadOnlyDictionary<string, string> pages) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Observe cancellation the way a real transport would, so a canceled walk fails deterministically.
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;

            return Task.FromResult(pages.TryGetValue(request.RequestUri!.PathAndQuery, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/hal+json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"title":"Not Found"}""", Encoding.UTF8, "application/problem+json") });
        }
    }
}
