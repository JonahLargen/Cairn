using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Client branches on the response path and in HAL-FORMS validation: 304s with and without an ETag,
// collection envelopes of unexpected shapes, diagnostics for bodies without a Content-Type, snippet
// truncation, whitespace-padded bodies, and every validation error both alone and after another error.
public class CairnClientBranchTests
{
    [Fact]
    public async Task A_304_without_an_etag_is_a_not_modified_success_with_no_etag()
    {
        var http = new HttpClient(new StatusHandler(HttpStatusCode.NotModified)) { BaseAddress = new Uri("http://localhost") };
        var client = new CairnClient(http);

        var result = await client.GetAsync<JsonElement>("/thing", ifNoneMatch: "\"v1\"");

        Assert.True(result.IsSuccess);
        Assert.True(result.IsNotModified);
        Assert.Null(result.Resource!.ETag);
    }

    [Fact]
    public async Task A_304_with_an_etag_preserves_it_on_the_empty_resource()
    {
        var handler = new StatusHandler(HttpStatusCode.NotModified) { ETag = "\"v2\"" };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new CairnClient(http);

        var result = await client.GetAsync<JsonElement>("/thing", ifNoneMatch: "\"v2\"");

        Assert.True(result.IsNotModified);
        Assert.Equal("\"v2\"", result.Resource!.ETag);
    }

    [Theory]
    [InlineData("""{ "name": "no items property" }""")]
    [InlineData("""{ "items": 5 }""")]
    [InlineData("42")]
    public async Task A_collection_body_without_an_items_array_yields_an_empty_collection(string body)
    {
        var client = NewClient(body, "application/json");

        // An envelope missing the items property, an items property that isn't an array, and a scalar
        // root all degrade to an empty page rather than throwing.
        var result = await client.GetCollectionAsync<JsonElement>("/things");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Collection!.Items);
    }

    [Fact]
    public async Task A_parse_failure_without_a_content_type_reports_it_as_unknown()
    {
        var client = NewClient("not json", contentType: null);

        var result = await client.GetAsync<JsonElement>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Equal("The response body is not valid JSON.", result.Problem!.Title);
        Assert.Contains("'unknown'", result.Problem.Detail);
    }

    [Fact]
    public async Task A_bind_failure_without_a_content_type_reports_it_as_unknown()
    {
        var client = NewClient("""{ "id": "not a number" }""", contentType: null);

        var result = await client.GetAsync<BranchThing>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Equal("The response body could not be bound to 'BranchThing'.", result.Problem!.Title);
        Assert.Contains("'unknown'", result.Problem.Detail);
    }

    [Fact]
    public async Task A_long_offending_body_is_truncated_in_the_snippet()
    {
        var client = NewClient(new string('x', 300), "text/plain");

        var result = await client.GetAsync<JsonElement>("/thing");

        Assert.False(result.IsSuccess);
        Assert.Contains(new string('x', 120) + "…", result.Problem!.Detail);
        Assert.DoesNotContain(new string('x', 121), result.Problem.Detail);
    }

    [Fact]
    public async Task A_body_padded_with_every_whitespace_kind_still_parses()
    {
        var client = NewClient(" \t\r\n{ \"id\": 7 }", "application/json");

        var result = await client.GetAsync<BranchThing>("/thing");

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value!.Id);
    }

    [Fact]
    public async Task An_all_whitespace_body_is_blank_not_malformed_json()
    {
        var client = NewClient(" \t\r\n", "application/json");

        var result = await client.GetAsync<BranchThing>("/thing");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Every_violation_still_reports_when_it_follows_another_error()
    {
        var client = NewClient("{}", "application/json");
        var affordance = new Affordance("act", "/act", "POST");
        var fields = new[]
        {
            new AffordanceField("first") { Required = true },
            new AffordanceField("locked") { ReadOnly = true },
            new AffordanceField("empty") { Required = true, MinLength = 3 },
            new AffordanceField("short") { MinLength = 5 },
            new AffordanceField("long") { MaxLength = 2 },
            new AffordanceField("coded") { Regex = "[a-z]+" },
            new AffordanceField("low") { Min = 5 },
            new AffordanceField("high") { Max = 5 },
        };

        // "first" is missing, so every later violation appends to an already-started error list.
        var values = new { locked = "x", empty = "", @short = "ab", @long = "abc", coded = "123", low = 1, high = 9 };
        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitAsync(affordance, fields, values));

        Assert.Contains("'first' is required", thrown.Message);
        Assert.Contains("'locked' is read-only", thrown.Message);
        Assert.Contains("'empty' is required", thrown.Message);
        Assert.Contains("'short' must be at least 5 characters", thrown.Message);
        Assert.Contains("'long' must be at most 2 characters", thrown.Message);
        Assert.Contains("'coded' must match the pattern", thrown.Message);
        Assert.Contains("'low' must be at least 5", thrown.Message);
        Assert.Contains("'high' must be at most 5", thrown.Message);
    }

    [Fact]
    public async Task Each_violation_also_reports_when_it_is_the_only_error()
    {
        var client = NewClient("{}", "application/json");
        var affordance = new Affordance("act", "/act", "POST");

        // Each case has exactly one failing field, so its error is the one that starts the list.
        await AssertSingleViolationAsync(client, affordance, new AffordanceField("locked") { ReadOnly = true }, new { locked = "x" }, "'locked' is read-only");
        await AssertSingleViolationAsync(client, affordance, new AffordanceField("empty") { Required = true }, new { empty = "" }, "'empty' is required");
        await AssertSingleViolationAsync(client, affordance, new AffordanceField("short") { MinLength = 5 }, new { @short = "ab" }, "'short' must be at least 5 characters");
        await AssertSingleViolationAsync(client, affordance, new AffordanceField("long") { MaxLength = 2 }, new { @long = "abc" }, "'long' must be at most 2 characters");
        await AssertSingleViolationAsync(client, affordance, new AffordanceField("coded") { Regex = "[a-z]+" }, new { coded = "123" }, "'coded' must match the pattern");
        await AssertSingleViolationAsync(client, affordance, new AffordanceField("low") { Min = 5 }, new { low = 1 }, "'low' must be at least 5");
        await AssertSingleViolationAsync(client, affordance, new AffordanceField("high") { Max = 5 }, new { high = 9 }, "'high' must be at most 5");
    }

    [Fact]
    public async Task Values_inside_every_constraint_pass_validation()
    {
        var client = NewClient("{}", "application/json");
        var affordance = new Affordance("act", "/act", "POST");
        var fields = new[]
        {
            new AffordanceField("text") { MinLength = 2, MaxLength = 10, Regex = "[a-z]+" },
            new AffordanceField("blank") { MinLength = 2 },
            new AffordanceField("number") { Min = 1, Max = 5 },
        };

        // An empty optional value is "not provided" (mirroring HTML minlength), and in-range values
        // satisfy their bounds — nothing is rejected client-side.
        var result = await client.SubmitAsync(affordance, fields, new { text = "fine", blank = "", number = 3 });

        Assert.True(result.IsSuccess);
    }

    private static async Task AssertSingleViolationAsync(
        CairnClient client, Affordance affordance, AffordanceField field, object values, string expected)
    {
        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitAsync(affordance, [field], values));
        Assert.Contains(expected, thrown.Message);
    }

    private static CairnClient NewClient(string body, string? contentType)
    {
        var http = new HttpClient(new StubHandler(body, contentType)) { BaseAddress = new Uri("http://localhost") };
        return new CairnClient(http);
    }

    private sealed record BranchThing(int Id);

    private sealed class StubHandler(string body, string? contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // ByteArrayContent carries no Content-Type header unless one is set, unlike StringContent.
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            if (contentType is not null)
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? ETag { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status);
            if (ETag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", ETag);
            }

            return Task.FromResult(response);
        }
    }
}
