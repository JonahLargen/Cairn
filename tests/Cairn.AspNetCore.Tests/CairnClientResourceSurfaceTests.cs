using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// The navigation surface of Resource<T> and CollectionResource<TItem>: embedded lookups that find
// nothing, templated follows from a resource, typed invokes, and collection-level affordances.
public class CairnClientResourceSurfaceTests
{
    [Fact]
    public async Task Embedded_returns_empty_when_the_document_has_no_embedded_section()
    {
        var resource = await GetResourceAsync("""{"name":"solo"}""");

        Assert.Empty(resource.Embedded<JsonElement>("items"));
    }

    [Fact]
    public async Task Embedded_returns_empty_for_an_unknown_relation()
    {
        var resource = await GetResourceAsync("""{"_embedded":{"orders":{"id":1}}}""");

        Assert.Empty(resource.Embedded<JsonElement>("customers"));
    }

    [Fact]
    public async Task A_resource_follows_a_templated_link_with_variables()
    {
        var handler = new RoutedHandler
        {
            ["/doc"] = """{"_links":{"search":{"href":"/s{?page}","templated":true}}}""",
            ["/s"] = """{"found":true}""",
        };
        var resource = await GetResourceAsync(handler, "/doc");

        var result = await resource.FollowAsync<JsonElement>("search", new { page = 2 });

        Assert.True(result.IsSuccess);
        Assert.Equal("?page=2", handler.LastRequestUri!.Query);
    }

    [Fact]
    public async Task Following_an_unknown_relation_with_variables_throws()
    {
        var resource = await GetResourceAsync("""{"name":"solo"}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resource.FollowAsync<JsonElement>("missing", new { page = 2 }));

        Assert.Contains("'missing'", exception.Message);
    }

    [Fact]
    public async Task A_resource_invokes_a_named_affordance_and_reads_the_result()
    {
        var handler = new RoutedHandler
        {
            ["/doc"] = """{"_templates":{"approve":{"method":"POST","target":"/approve"}}}""",
            ["/approve"] = """{"approved":true}""",
        };
        var resource = await GetResourceAsync(handler, "/doc");

        var result = await resource.InvokeAsync<JsonElement>("approve");

        Assert.True(result.IsSuccess);
        Assert.True(result.Resource!.RequireValue().GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task Invoking_an_unknown_affordance_for_a_result_throws()
    {
        var resource = await GetResourceAsync("""{"name":"solo"}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resource.InvokeAsync<JsonElement>("missing"));

        Assert.Contains("'missing'", exception.Message);
    }

    [Fact]
    public async Task A_collection_exposes_its_affordances_and_link_arrays()
    {
        var handler = new RoutedHandler
        {
            ["/items"] = """
                {
                  "items": [],
                  "_links": {"mirror": [{"href":"/a"},{"href":"/b"}]},
                  "_templates": {"purge": {"method": "POST", "target": "/purge"}}
                }
                """,
        };
        var client = NewClient(handler);

        var collection = (await client.GetCollectionAsync<JsonElement>("/items")).EnsureSuccess();

        Assert.True(collection.HasAffordance("purge"));
        Assert.False(collection.HasAffordance("missing"));
        Assert.Equal("POST", collection.Affordances["purge"].Method);
        Assert.Equal(2, collection.LinksFor("mirror").Count);
        Assert.Empty(collection.LinksFor("missing"));
    }

    [Fact]
    public async Task A_collection_invokes_a_named_affordance()
    {
        var handler = new RoutedHandler
        {
            ["/items"] = """{"items":[],"_templates":{"purge":{"method":"POST","target":"/purge"}}}""",
            ["/purge"] = "{}",
        };
        var client = NewClient(handler);
        var collection = (await client.GetCollectionAsync<JsonElement>("/items")).EnsureSuccess();

        var result = await collection.InvokeAsync("purge");

        Assert.True(result.IsSuccess);
        Assert.Equal("/purge", handler.LastRequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Invoking_an_unknown_collection_affordance_throws()
    {
        var handler = new RoutedHandler { ["/items"] = """{"items":[]}""" };
        var client = NewClient(handler);
        var collection = (await client.GetCollectionAsync<JsonElement>("/items")).EnsureSuccess();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => collection.InvokeAsync("missing"));

        Assert.Contains("'missing'", exception.Message);
    }

    private static async Task<Resource<JsonElement>> GetResourceAsync(string document)
        => await GetResourceAsync(new RoutedHandler { ["/doc"] = document }, "/doc");

    private static async Task<Resource<JsonElement>> GetResourceAsync(RoutedHandler handler, string url)
    {
        var client = NewClient(handler);
        return (await client.GetAsync<JsonElement>(url)).EnsureSuccess();
    }

    private static CairnClient NewClient(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });

    private sealed class RoutedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = [];

        public Uri? LastRequestUri { get; private set; }

        public string this[string path]
        {
            set => _responses[path] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var body = _responses.TryGetValue(request.RequestUri!.AbsolutePath, out var canned) ? canned : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
