using BenchmarkDotNet.Attributes;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cairn.Benchmarks;

/// <summary>
/// Answers "what does Cairn cost per response?" end to end: the same 1,000-item page served through an
/// in-process server without Cairn (baseline), with the endpoint filter + compute + emit pipeline, and with
/// the filter attached but the item type unconfigured (isolating the filter and the per-object emit-stage
/// lookups from link computation). Single-resource variants show the per-request floor.
/// </summary>
[MemoryDiagnoser]
public class EndToEndBenchmarks
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options => options.AddLinks(new OrderLinks()));

        _app = builder.Build();

        var items = Enumerable.Range(1, 1000).Select(i => new OrderDto(i, i % 5)).ToArray();
        var unconfigured = Enumerable.Range(1, 1000).Select(i => new UnconfiguredDto(i)).ToArray();

        _app.MapGet("/plain", () => TypedResults.Ok(new PagedResource<OrderDto>(items, Page: 2, PageSize: 1000, TotalCount: 5000)));
        _app.MapGet("/linked", () => TypedResults.Ok(new PagedResource<OrderDto>(items, Page: 2, PageSize: 1000, TotalCount: 5000)))
            .WithLinks();
        _app.MapGet("/unconfigured", () => TypedResults.Ok(new PagedResource<UnconfiguredDto>(unconfigured, Page: 2, PageSize: 1000, TotalCount: 5000)))
            .WithLinks();

        _app.MapGet("/one-plain", () => TypedResults.Ok(new OrderDto(1, 1)));
        _app.MapGet("/one-linked", () => TypedResults.Ok(new OrderDto(1, 1))).WithLinks();

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

    [Benchmark(Baseline = true, Description = "1000-item page, no Cairn")]
    public Task<byte[]> Page1000Plain() => _client.GetByteArrayAsync("/plain");

    [Benchmark(Description = "1000-item page, WithLinks + configs")]
    public Task<byte[]> Page1000Linked() => _client.GetByteArrayAsync("/linked");

    [Benchmark(Description = "1000-item page, WithLinks, unconfigured items")]
    public Task<byte[]> Page1000Unconfigured() => _client.GetByteArrayAsync("/unconfigured");

    [Benchmark(Description = "single resource, no Cairn")]
    public Task<byte[]> SinglePlain() => _client.GetByteArrayAsync("/one-plain");

    [Benchmark(Description = "single resource, WithLinks + config")]
    public Task<byte[]> SingleLinked() => _client.GetByteArrayAsync("/one-linked");
}
