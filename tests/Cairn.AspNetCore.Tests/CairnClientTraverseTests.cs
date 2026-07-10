using System.Net;
using System.Text;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// TraverseAsync is Traverson-style multi-hop sugar: follow a chain of relations in one call —
// e.g. TraverseAsync<OrderItem>("orders", "next", "item") — binding only the final response.
// Intermediate hops are read for hypermedia only; the per-hop error model matches single-hop follows.
public class CairnClientTraverseTests
{
    private const string Root = """
        {"_links":{"self":{"href":"/"},"orders":{"href":"/orders"}}}
        """;

    private const string Orders = """
        {"_links":{"self":{"href":"/orders"},"next":{"href":"/orders?page=2"}}}
        """;

    private const string OrdersPage2 = """
        {"_links":{"self":{"href":"/orders?page=2"},"item":{"href":"/orders/42"}}}
        """;

    private const string Order42 = """
        {"id":42,"note":"rush","_links":{"self":{"href":"/orders/42"},"customer":{"href":"/customers/7"}}}
        """;

    private static Dictionary<string, string> OrderChain() => new()
    {
        ["/"] = Root,
        ["/orders"] = Orders,
        ["/orders?page=2"] = OrdersPage2,
        ["/orders/42"] = Order42,
    };

    [Fact]
    public async Task Client_traverse_follows_each_relation_and_binds_the_final_resource()
    {
        var handler = new RoutingHandler(OrderChain());
        var client = new CairnClient(Client(handler));

        var result = await client.TraverseAsync<Order>("/", "orders", "next", "item");

        var order = result.EnsureSuccess();
        Assert.Equal(42, order.RequireValue().Id);
        Assert.True(order.HasLink("customer"));                       // the final resource keeps its own hypermedia.
        Assert.Equal(4, handler.Requests);                            // the start URL plus one request per relation.
        Assert.Equal(new[] { "/", "/orders", "/orders?page=2", "/orders/42" }, handler.Paths);
    }

    [Fact]
    public async Task Resource_traverse_starts_from_the_in_hand_links_without_refetching()
    {
        var handler = new RoutingHandler(OrderChain());
        var client = new CairnClient(Client(handler));
        var root = (await client.GetAsync<Empty>("/")).EnsureSuccess();
        handler.Reset();

        var result = await root.TraverseAsync<Order>("orders", "next", "item");

        Assert.Equal(42, result.EnsureSuccess().RequireValue().Id);
        Assert.Equal(3, handler.Requests);   // "orders" resolved from the in-hand resource — no refetch of "/".
        Assert.Equal(new[] { "/orders", "/orders?page=2", "/orders/42" }, handler.Paths);
    }

    [Fact]
    public async Task Collection_traverse_starts_from_the_collection_links()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/orders"] = """{"items":[],"_links":{"next":{"href":"/orders?page=2"}}}""",
            ["/orders?page=2"] = OrdersPage2,
            ["/orders/42"] = Order42,
        });
        var client = new CairnClient(Client(handler));
        var orders = (await client.GetCollectionAsync<Order>("/orders")).EnsureSuccess();

        var result = await orders.TraverseAsync<Order>("next", "item");

        Assert.Equal(42, result.EnsureSuccess().RequireValue().Id);
    }

    [Fact]
    public async Task A_single_relation_traverse_is_a_plain_follow()
    {
        var handler = new RoutingHandler(OrderChain());
        var client = new CairnClient(Client(handler));
        var page = (await client.GetAsync<Empty>("/orders?page=2")).EnsureSuccess();
        handler.Reset();

        var result = await page.TraverseAsync<Order>("item");

        Assert.Equal(42, result.EnsureSuccess().RequireValue().Id);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task An_intermediate_error_status_ends_the_traversal_with_that_hops_failure()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = """{"_links":{"orders":{"href":"/gone"}}}""",   // "/gone" is not served — the hop gets a 404.
        });
        var client = new CairnClient(Client(handler));

        var result = await client.TraverseAsync<Order>("/", "orders", "next", "item");

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.Status);
        Assert.NotNull(result.Problem);
        Assert.Equal(2, handler.Requests);   // the traversal stopped at the failing hop.
    }

    [Fact]
    public async Task A_final_hop_error_status_surfaces_as_the_result_failure()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = Root,
            ["/orders"] = """{"_links":{"item":{"href":"/missing"}}}""",
        });
        var client = new CairnClient(Client(handler));

        var result = await client.TraverseAsync<Order>("/", "orders", "item");

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.Status);
    }

    [Fact]
    public async Task An_unparsable_intermediate_body_fails_as_a_problem_not_an_exception()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = """{"_links":{"orders":{"href":"/orders"}}}""",
            ["/orders"] = "<!doctype html><p>not json</p>",
        });
        var client = new CairnClient(Client(handler));

        var result = await client.TraverseAsync<Order>("/", "orders", "item");

        Assert.False(result.IsSuccess);
        Assert.Equal(200, result.Status);
        Assert.Equal("The response body is not valid JSON.", result.Problem!.Title);
    }

    [Fact]
    public async Task A_missing_relation_mid_chain_throws_naming_the_relation_and_the_path()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = Root,
            ["/orders"] = """{"_links":{"self":{"href":"/orders"}}}""",   // no "next" here.
        });
        var client = new CairnClient(Client(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.TraverseAsync<Order>("/", "orders", "next", "item"));

        Assert.Contains("'next'", exception.Message);
        Assert.Contains("'orders'", exception.Message);   // the path that reached the linkless resource.
    }

    [Fact]
    public async Task A_missing_first_relation_on_the_start_document_names_the_starting_resource()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = """{"_links":{"self":{"href":"/"}}}""",
        });
        var client = new CairnClient(Client(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.TraverseAsync<Order>("/", "orders", "item"));

        Assert.Contains("'orders'", exception.Message);
        Assert.StartsWith("The starting resource", exception.Message);
    }

    [Fact]
    public async Task A_missing_first_relation_on_an_in_hand_resource_throws_the_single_hop_message()
    {
        var handler = new RoutingHandler(OrderChain());
        var client = new CairnClient(Client(handler));
        var root = (await client.GetAsync<Empty>("/")).EnsureSuccess();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => root.TraverseAsync<Order>("nope", "item"));

        Assert.Equal("The resource has no 'nope' link.", exception.Message);
    }

    [Fact]
    public async Task Relations_match_case_insensitively_like_single_hop_follows()
    {
        var handler = new RoutingHandler(OrderChain());
        var client = new CairnClient(Client(handler));

        var result = await client.TraverseAsync<Order>("/", "Orders", "NEXT", "Item");

        Assert.Equal(42, result.EnsureSuccess().RequireValue().Id);
    }

    [Fact]
    public async Task A_templated_link_along_the_chain_collapses_with_no_variables()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = """{"_links":{"orders":{"href":"/orders{?page}","templated":true}}}""",
            ["/orders"] = """{"_links":{"item":{"href":"/orders/42"}}}""",
            ["/orders/42"] = Order42,
        });
        var client = new CairnClient(Client(handler));

        var result = await client.TraverseAsync<Order>("/", "orders", "item");

        Assert.Equal(42, result.EnsureSuccess().RequireValue().Id);
        Assert.Contains("/orders", handler.Paths);   // the optional {?page} expression collapsed per RFC 6570.
    }

    [Fact]
    public async Task The_link_policy_is_enforced_on_every_hop()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = """{"_links":{"orders":{"href":"http://evil.example/orders"}}}""",
        });
        var client = new CairnClient(Client(handler), allowLink: uri => uri.Host == "localhost");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.TraverseAsync<Order>("/", "orders", "item"));

        Assert.Contains("not permitted", exception.Message);
        Assert.Equal(1, handler.Requests);   // rejected before the off-host hop was fetched.
    }

    [Fact]
    public async Task When_a_relation_has_a_link_array_the_first_link_is_followed()
    {
        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            ["/"] = """{"_links":{"orders":[{"href":"/orders/42"},{"href":"/orders/43"}]}}""",
            ["/orders/42"] = Order42,
        });
        var client = new CairnClient(Client(handler));

        var result = await client.TraverseAsync<Order>("/", "orders");

        Assert.Equal(42, result.EnsureSuccess().RequireValue().Id);
    }

    [Fact]
    public async Task Null_relations_throw_eagerly()
    {
        var client = new CairnClient(Client(new RoutingHandler(OrderChain())));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.TraverseAsync<Order>("/", relations: null!));
    }

    [Fact]
    public async Task Empty_relations_throw_eagerly()
    {
        var client = new CairnClient(Client(new RoutingHandler(OrderChain())));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.TraverseAsync<Order>("/"));
        Assert.Equal("relations", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_null_or_empty_relation_in_the_chain_throws_eagerly(string? bad)
    {
        var handler = new RoutingHandler(OrderChain());
        var client = new CairnClient(Client(handler));
        var root = (await client.GetAsync<Empty>("/")).EnsureSuccess();
        handler.Reset();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => root.TraverseAsync<Order>("orders", bad!));

        Assert.Equal("relations", exception.ParamName);
        Assert.Equal(0, handler.Requests);   // validation fires before anything is sent.
    }

    private static HttpClient Client(RoutingHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost") };

    private sealed record Order(int Id, string? Note);

    private sealed record Empty;

    private sealed class RoutingHandler(IReadOnlyDictionary<string, string> pages) : HttpMessageHandler
    {
        private readonly List<string> _paths = [];

        public int Requests => _paths.Count;

        public IReadOnlyList<string> Paths => _paths;

        public void Reset() => _paths.Clear();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _paths.Add(request.RequestUri!.PathAndQuery);

            return Task.FromResult(pages.TryGetValue(request.RequestUri!.PathAndQuery, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/hal+json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"title":"Not Found"}""", Encoding.UTF8, "application/problem+json") });
        }
    }
}
