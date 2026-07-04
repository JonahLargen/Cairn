using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Problem parsing branches for the error bodies a server can actually send: non-JSON, JSON that is not
// an object, members carrying the wrong JSON kind, and a fully-populated RFC 9457 document.
public class ProblemReaderBranchTests
{
    [Fact]
    public async Task A_non_json_error_body_falls_back_to_the_status_problem()
    {
        var result = await GetProblemAsync("definitely not json");

        Assert.Equal("Bad Request", result.Title);
        Assert.Equal(400, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task A_json_array_error_body_falls_back_to_the_status_problem()
    {
        // Valid JSON, but not an object — there are no problem members to read.
        var result = await GetProblemAsync("""[ "oops" ]""");

        Assert.Equal("Bad Request", result.Title);
        Assert.Equal(400, result.Status);
    }

    [Fact]
    public async Task An_empty_problem_object_takes_the_reason_phrase_and_status()
    {
        var result = await GetProblemAsync("{}");

        Assert.Equal("Bad Request", result.Title);
        Assert.Equal(400, result.Status);
        Assert.Empty(result.Extensions);
    }

    [Fact]
    public async Task Problem_members_of_the_wrong_json_kind_fall_back()
    {
        // A numeric detail is not a string and a string status is not a number; both yield the fallback.
        var result = await GetProblemAsync("""{ "detail": 7, "status": "418" }""");

        Assert.Null(result.Detail);
        Assert.Equal(400, result.Status);
    }

    [Fact]
    public async Task A_fractional_status_falls_back_to_the_response_status()
    {
        var result = await GetProblemAsync("""{ "status": 418.5 }""");

        Assert.Equal(400, result.Status);
    }

    [Fact]
    public async Task Every_standard_member_parses_and_only_extensions_are_kept_aside()
    {
        const string body = """
            {
              "type": "https://example.com/probs/out-of-credit",
              "title": "You do not have enough credit.",
              "status": 403,
              "detail": "Your balance is 30, but the cost is 50.",
              "instance": "/account/12345/msgs/abc",
              "balance": 30
            }
            """;
        var result = await GetProblemAsync(body);

        Assert.Equal("https://example.com/probs/out-of-credit", result.Type);
        Assert.Equal("You do not have enough credit.", result.Title);
        Assert.Equal(403, result.Status);
        Assert.Equal("Your balance is 30, but the cost is 50.", result.Detail);
        Assert.Equal("/account/12345/msgs/abc", result.Instance);
        Assert.Equal(30, Assert.Single(result.Extensions).Value.GetInt32());
    }

    private static async Task<Problem> GetProblemAsync(string body)
    {
        var http = new HttpClient(new ErrorStub(body)) { BaseAddress = new Uri("http://localhost") };
        var result = await new CairnClient(http).GetAsync<JsonElement>("/thing");

        Assert.False(result.IsSuccess);
        return result.Problem!;
    }

    private sealed class ErrorStub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
            });
    }
}
