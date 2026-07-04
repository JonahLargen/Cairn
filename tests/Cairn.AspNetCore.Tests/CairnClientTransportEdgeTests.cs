using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Transport-level corners of CairnClient: the whole-exchange timeout, error-body charset handling,
// and collection requests that fail at the HTTP or binding layer.
public class CairnClientTransportEdgeTests
{
    [Fact]
    public async Task Following_a_non_templated_link_with_null_variables_uses_the_href()
    {
        var handler = new CannedHandler(_ => Json("{}"));
        var client = NewClient(handler);

        var result = await client.FollowAsync<JsonElement>(new Link("self", "/plain"), (object?)null);

        Assert.True(result.IsSuccess);
        Assert.Equal("/plain", handler.LastRequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_collection_error_status_surfaces_the_problem()
    {
        var handler = new CannedHandler(_ => Problem(HttpStatusCode.Forbidden, """{"title":"No access"}"""));
        var client = NewClient(handler);

        var result = await client.GetCollectionAsync<JsonElement>("/items");

        Assert.False(result.IsSuccess);
        Assert.Equal(403, result.Status);
        Assert.Equal("No access", result.Problem!.Title);
    }

    [Fact]
    public async Task A_collection_item_that_cannot_bind_surfaces_a_problem_not_an_exception()
    {
        var handler = new CannedHandler(_ => Json("""{"items":["not-a-number"]}"""));
        var client = NewClient(handler);

        var result = await client.GetCollectionAsync<int>("/items");

        Assert.False(result.IsSuccess);
        Assert.Contains("could not be bound to 'Int32'", result.Problem!.Title);
    }

    [Fact]
    public async Task The_timeout_covers_the_whole_exchange_and_reports_a_TimeoutException()
    {
        var handler = new CannedHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return await Json("{}");
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromMilliseconds(100),
        };
        var client = new CairnClient(http);

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync<JsonElement>("/slow"));

        // HttpClient's own convention: a timeout is a TaskCanceledException carrying a TimeoutException,
        // so callers can still tell it apart from their own cancellation.
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact]
    public async Task An_error_body_in_a_declared_charset_is_decoded_with_it()
    {
        var handler = new CannedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ByteArrayContent(Encoding.Unicode.GetBytes("""{"title":"Décodage"}""")),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/problem+json; charset=utf-16");
            return Task.FromResult(response);
        });
        var client = NewClient(handler);

        var result = await client.GetAsync<JsonElement>("/err");

        Assert.False(result.IsSuccess);
        Assert.Equal("Décodage", result.Problem!.Title);
    }

    [Fact]
    public async Task An_unknown_charset_falls_back_to_utf8()
    {
        var handler = new CannedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"title":"Fallback"}""")),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/problem+json; charset=banana");
            return Task.FromResult(response);
        });
        var client = NewClient(handler);

        var result = await client.GetAsync<JsonElement>("/err");

        Assert.False(result.IsSuccess);
        Assert.Equal("Fallback", result.Problem!.Title);
    }

    private static CairnClient NewClient(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });

    private static Task<HttpResponseMessage> Json(string body)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static Task<HttpResponseMessage> Problem(HttpStatusCode status, string body)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
        });

    private sealed class CannedHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return await respond(cancellationToken);
        }
    }
}
