using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// A 2xx response whose body is not the JSON the client expected must surface through the result/exception
// contract (a failed result with a Problem, or a CairnClientException from EnsureSuccess) — never as a raw
// JsonException escaping to the caller.
public class CairnClientParseFailureTests
{
    [Fact]
    public async Task A_success_response_with_a_non_json_body_is_a_failed_result_not_a_json_exception()
    {
        var client = NewClient("<html><body>maintenance page</body></html>", "text/html");

        var result = await client.GetAsync<ParseThing>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Equal(200, result.Status);
        Assert.False(result.IsNotModified);
        Assert.Equal("The response body is not valid JSON.", result.Problem!.Title);
        Assert.Contains("text/html", result.Problem.Detail);
        Assert.Contains("<html>", result.Problem.Detail);   // a snippet of the offending body

        var thrown = Assert.Throws<CairnClientException>(() => result.EnsureSuccess());
        Assert.Equal(200, thrown.Status);
    }

    [Fact]
    public async Task A_success_response_with_malformed_json_is_a_failed_result()
    {
        var client = NewClient("{ \"id\": 1, ", "application/json");

        var result = await client.GetAsync<ParseThing>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task A_body_that_cannot_bind_to_the_value_type_is_a_failed_result()
    {
        var client = NewClient("{ \"id\": \"not a number\" }", "application/json");

        var result = await client.GetAsync<ParseThing>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task A_collection_response_with_malformed_json_is_a_failed_result()
    {
        var client = NewClient("not json at all", "text/plain");

        var result = await client.GetCollectionAsync<ParseThing>("/things");

        Assert.False(result.IsSuccess);
        Assert.Equal(200, result.Status);
        Assert.Equal("The response body is not valid JSON.", result.Problem!.Title);
        Assert.Throws<CairnClientException>(() => result.EnsureSuccess());
    }

    [Fact]
    public async Task An_invoked_affordance_returning_malformed_json_is_a_failed_result()
    {
        var client = NewClient("oops", "text/plain");

        var result = await client.InvokeAsync<ParseThing>(new Affordance("act", "/act", "POST"));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Problem);
    }

    private static CairnClient NewClient(string body, string contentType)
    {
        var http = new HttpClient(new StubHandler(body, contentType)) { BaseAddress = new Uri("http://localhost") };
        return new CairnClient(http);
    }

    private sealed record ParseThing(int Id);

    private sealed class StubHandler(string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
    }
}
