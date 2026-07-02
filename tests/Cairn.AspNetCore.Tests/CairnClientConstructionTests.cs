using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

public class CairnClientConstructionTests
{
    [Fact]
    public void The_constructor_never_mutates_the_injected_http_clients_default_headers()
    {
        using var http = new HttpClient();

        _ = new CairnClient(http);

        // A shared, long-lived HttpClient must not change behavior for its other consumers, and
        // DefaultRequestHeaders is not safe to mutate with requests in flight.
        Assert.Empty(http.DefaultRequestHeaders.Accept);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Each_request_asks_for_the_hypermedia_the_client_can_parse()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new CairnClient(http);

        await client.GetAsync<JsonElement>("/thing");

        var accept = string.Join(", ", handler.LastRequestAccept!);
        Assert.Contains("application/prs.hal-forms+json", accept);
        Assert.Contains("application/hal+json", accept);
        Assert.Contains("application/json", accept);

        // The Accept header travels on the request, not on the shared client.
        Assert.Empty(http.DefaultRequestHeaders.Accept);
    }

    [Fact]
    public async Task A_caller_declared_default_accept_still_wins()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.custom+json");
        var client = new CairnClient(http);

        await client.GetAsync<JsonElement>("/thing");

        Assert.Equal("application/vnd.custom+json", string.Join(", ", handler.LastRequestAccept!));
    }

    [Fact]
    public void The_default_json_options_are_a_single_cached_instance()
    {
        using var http = new HttpClient();
        var first = new CairnClient(http);
        var second = new CairnClient(http);

        // Constructing options per client would discard System.Text.Json's per-options metadata cache.
        Assert.Same(GetJsonOptions(first), GetJsonOptions(second));
    }

    [Fact]
    public void Caller_supplied_json_options_are_used_as_is_not_copied()
    {
        using var http = new HttpClient();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var client = new CairnClient(http, options);

        Assert.Same(options, GetJsonOptions(client));
    }

    private static JsonSerializerOptions? GetJsonOptions(CairnClient client)
        => (JsonSerializerOptions?)typeof(CairnClient)
            .GetField("_json", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public IEnumerable<string>? LastRequestAccept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestAccept = request.Headers.Accept.Select(a => a.ToString()).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
