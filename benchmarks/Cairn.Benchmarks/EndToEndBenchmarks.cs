using BenchmarkDotNet.Attributes;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cairn.Benchmarks;

/// <summary>
/// Answers "what does Cairn cost per collection response?" end to end via TestHost, at a representative page
/// size (50) and a stress size (1000). The same page is served five ways: without links (floor), with links
/// hand-rolled in the handler (the real-world alternative), through the WithLinks pipeline with route-based
/// configs, with explicit-URI configs (skipping LinkGenerator), and with the filter attached but the item
/// type unconfigured (isolating the filter plus per-object emit-stage lookups from link computation).
///
/// Responses are drained to <see cref="Stream.Null"/> through a pooled copy buffer instead of buffered into
/// a byte[]: a linked payload is legitimately several times larger than the plain one, and buffering it
/// client-side would charge that size difference (including LOH churn above 85 KB) to the Allocated column,
/// conflating "the response carries more data" with the pipeline cost being measured.
/// </summary>
[MemoryDiagnoser]
public class EndToEndBenchmarks
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    /// <summary>50 approximates a typical API page; 1000 is a deliberate stress case.</summary>
    [Params(50, 1000)]
    public int PageItems { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options => options
            .AddLinks(new OrderLinks())
            .AddLinks(new UriOrderLinks()));

        _app = builder.Build();

        var totalCount = PageItems * 5;
        var items = Enumerable.Range(1, PageItems).Select(i => new OrderDto(i, i % 5)).ToArray();
        var uriItems = Enumerable.Range(1, PageItems).Select(i => new UriOrderDto(i, i % 5)).ToArray();
        var unconfigured = Enumerable.Range(1, PageItems).Select(i => new UnconfiguredDto(i)).ToArray();

        _app.MapGet("/plain", () => TypedResults.Ok(new PagedResource<OrderDto>(items, Page: 2, PageSize: items.Length, TotalCount: totalCount)));

        // The projection runs per request, like it would in a real handler: link hrefs depend on the item ids
        // and the request's host, so a hand-rolling developer cannot precompute them at startup.
        _app.MapGet("/hand-rolled", (HttpContext http) =>
        {
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            var projected = new HandRolledOrderDto[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                var order = items[i];
                projected[i] = new HandRolledOrderDto(order.Id, order.Status)
                {
                    Links = new Dictionary<string, HandRolledLink>(2)
                    {
                        ["self"] = new($"{baseUrl}/orders/{order.Id}"),
                        ["collection"] = new("/orders"),
                    },
                    Actions = order.Status == 1
                        ? new Dictionary<string, HandRolledAction>(1) { ["cancel"] = new($"{baseUrl}/orders/{order.Id}/cancel", "POST") }
                        : null,
                };
            }

            var pageUrl = $"{baseUrl}{http.Request.Path}";
            return TypedResults.Ok(new HandRolledPage<HandRolledOrderDto>(
                projected,
                Page: 2,
                PageSize: items.Length,
                TotalCount: totalCount,
                Links: new Dictionary<string, HandRolledLink>(5)
                {
                    ["self"] = new($"{pageUrl}?page=2"),
                    ["first"] = new($"{pageUrl}?page=1"),
                    ["last"] = new($"{pageUrl}?page=5"),
                    ["prev"] = new($"{pageUrl}?page=1"),
                    ["next"] = new($"{pageUrl}?page=3"),
                }));
        });

        _app.MapGet("/linked", () => TypedResults.Ok(new PagedResource<OrderDto>(items, Page: 2, PageSize: items.Length, TotalCount: totalCount)))
            .WithLinks();
        _app.MapGet("/linked-uri", () => TypedResults.Ok(new PagedResource<UriOrderDto>(uriItems, Page: 2, PageSize: uriItems.Length, TotalCount: totalCount)))
            .WithLinks();
        _app.MapGet("/unconfigured", () => TypedResults.Ok(new PagedResource<UnconfiguredDto>(unconfigured, Page: 2, PageSize: unconfigured.Length, TotalCount: totalCount)))
            .WithLinks();

        _app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new OrderDto(id, 1))).WithName("BenchGetOrder");
        _app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("BenchCancelOrder");

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Benchmark(Baseline = true, Description = "page, no links")]
    public Task PagePlain() => DrainAsync("/plain");

    [Benchmark(Description = "page, hand-rolled links")]
    public Task PageHandRolled() => DrainAsync("/hand-rolled");

    [Benchmark(Description = "page, WithLinks + route configs")]
    public Task PageLinked() => DrainAsync("/linked");

    [Benchmark(Description = "page, WithLinks + explicit-URI configs")]
    public Task PageLinkedExplicitUri() => DrainAsync("/linked-uri");

    [Benchmark(Description = "page, WithLinks, unconfigured items")]
    public Task PageUnconfigured() => DrainAsync("/unconfigured");

    private async Task DrainAsync(string path)
    {
        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        await response.Content.CopyToAsync(Stream.Null);
    }
}
