using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Result and resource surface branches: missing links/affordances/embedded relations, the empty-resource
// throw of RequireValue, failure-side conveniences on the result types, and the exception message with
// and without a problem title.
public class CairnClientResultSurfaceTests
{
    private const string Doc = """
        {
          "id": 5,
          "_links": {
            "self": { "href": "/things/5" },
            "next": { "href": "/things/6" }
          },
          "_embedded": {
            "child": { "id": 6 },
            "scalar": "not a resource"
          },
          "_templates": {
            "rename": { "method": "POST", "target": "/things/5/rename", "properties": [] }
          }
        }
        """;

    [Fact]
    public async Task RequireValue_throws_on_the_empty_resource_a_304_carries()
    {
        var http = new HttpClient(new StatusStub(HttpStatusCode.NotModified)) { BaseAddress = new Uri("http://localhost") };
        var result = await new CairnClient(http).GetAsync<SurfaceThing>("/things/5", ifNoneMatch: "\"v1\"");

        Assert.True(result.IsNotModified);
        var thrown = Assert.Throws<InvalidOperationException>(() => result.Resource!.RequireValue());
        Assert.Contains("SurfaceThing", thrown.Message);
    }

    [Fact]
    public async Task LinksFor_returns_empty_for_an_unknown_relation()
    {
        var resource = await GetResourceAsync();

        Assert.Empty(resource.LinksFor("unknown"));
        Assert.Equal("/things/6", Assert.Single(resource.LinksFor("next")).Href);
    }

    [Fact]
    public async Task Embedded_resolves_a_single_object_and_ignores_a_scalar()
    {
        var resource = await GetResourceAsync();

        Assert.Equal(6, Assert.Single(resource.Embedded<SurfaceThing>("child")).RequireValue().Id);
        Assert.Empty(resource.Embedded<SurfaceThing>("scalar"));
    }

    [Fact]
    public async Task Invoking_an_unknown_affordance_by_name_throws()
    {
        var resource = await GetResourceAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.InvokeAsync("vanish"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.SubmitAsync<SurfaceThing>("vanish"));
    }

    [Fact]
    public async Task A_named_invoke_and_typed_submit_reach_the_affordance_target()
    {
        var resource = await GetResourceAsync();

        var invoked = await resource.InvokeAsync("rename");
        Assert.True(invoked.IsSuccess);

        var submitted = await resource.SubmitAsync<SurfaceThing>("rename");
        Assert.True(submitted.IsSuccess);
    }

    [Fact]
    public async Task Following_an_unknown_collection_relation_throws_in_both_overloads()
    {
        var http = new HttpClient(new BodyStub("""{ "items": [] }""")) { BaseAddress = new Uri("http://localhost") };
        var collection = (await new CairnClient(http).GetCollectionAsync<SurfaceThing>("/things")).EnsureSuccess();

        await Assert.ThrowsAsync<InvalidOperationException>(() => collection.FollowAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => collection.FollowAsync("missing", new { page = 2 }));
    }

    [Fact]
    public async Task The_value_shortcut_is_default_on_a_failed_result()
    {
        var http = new HttpClient(new StatusStub(HttpStatusCode.InternalServerError)) { BaseAddress = new Uri("http://localhost") };
        var result = await new CairnClient(http).GetAsync<SurfaceThing>("/things/5");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task EnsureSuccess_on_a_failed_bodiless_result_throws_with_the_status()
    {
        var http = new HttpClient(new StatusStub(HttpStatusCode.Conflict)) { BaseAddress = new Uri("http://localhost") };
        var result = await new CairnClient(http).InvokeAsync(new Affordance("act", "/act", "POST"));

        var thrown = Assert.Throws<CairnClientException>(result.EnsureSuccess);
        Assert.Equal(409, thrown.Status);
    }

    [Fact]
    public void The_exception_message_works_with_and_without_a_problem_title()
    {
        Assert.Equal("The request failed with status 500.", new CairnClientException(500, null).Message);
        Assert.Equal("The request failed with status 502.", new CairnClientException(502, new Problem()).Message);
        Assert.Equal(
            "The request failed with status 503: Down for maintenance.",
            new CairnClientException(503, new Problem { Title = "Down for maintenance." }).Message);
    }

    private static async Task<Resource<SurfaceThing>> GetResourceAsync()
    {
        var http = new HttpClient(new BodyStub(Doc)) { BaseAddress = new Uri("http://localhost") };
        return (await new CairnClient(http).GetAsync<SurfaceThing>("/things/5")).EnsureSuccess();
    }

    private sealed record SurfaceThing(int Id);

    private sealed class BodyStub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/prs.hal-forms+json") }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private sealed class StatusStub(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
