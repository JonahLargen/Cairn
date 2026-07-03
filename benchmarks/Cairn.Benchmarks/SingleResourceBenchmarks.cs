using BenchmarkDotNet.Attributes;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cairn.Benchmarks;

/// <summary>
/// The per-request floor: what WithLinks adds to a single-resource endpoint — the endpoint filter, format
/// negotiation, compute-stage scope setup, and one link computation. This is the most representative shape
/// of real API traffic; the absolute delta here (single-digit microseconds) is the number to weigh against
/// a handler's own work, and it is a deliberate non-goal to optimize further (see README.md).
/// </summary>
[MemoryDiagnoser]
public class SingleResourceBenchmarks
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

    [Benchmark(Baseline = true, Description = "single resource, no Cairn")]
    public Task SinglePlain() => DrainAsync("/one-plain");

    [Benchmark(Description = "single resource, WithLinks + config")]
    public Task SingleLinked() => DrainAsync("/one-linked");

    private async Task DrainAsync(string path)
    {
        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        await response.Content.CopyToAsync(Stream.Null);
    }
}
