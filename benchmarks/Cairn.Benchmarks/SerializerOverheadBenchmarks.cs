using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cairn.Benchmarks;

/// <summary>
/// Isolates the emit-stage overhead Cairn's contract modifier adds to serialization itself: every object
/// serialized under Cairn-wired options carries injected hypermedia properties whose getters do a
/// per-property store lookup. Serializing 1,000 DTOs outside a request (no HttpContext, the cheapest path)
/// against vanilla System.Text.Json shows the floor of that per-object cost.
/// </summary>
[MemoryDiagnoser]
public class SerializerOverheadBenchmarks
{
    private readonly JsonSerializerOptions _vanilla = new(JsonSerializerDefaults.Web);
    private JsonSerializerOptions _cairn = null!;
    private List<OrderDto> _items = null!;
    private Stream _sink = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCairn(options => options.AddLinks(new OrderLinks()));
        var provider = services.BuildServiceProvider();

        // The post-configured minimal-API options carry Cairn's link-injection modifier; with no current
        // HttpContext every injected property short-circuits after the accessor check.
        _cairn = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

        _items = Enumerable.Range(1, 1000).Select(i => new OrderDto(i, i % 5)).ToList();
        _sink = Stream.Null;
    }

    [Benchmark(Baseline = true, Description = "1000 DTOs, vanilla STJ")]
    public Task Vanilla() => JsonSerializer.SerializeAsync(_sink, _items, _vanilla);

    [Benchmark(Description = "1000 DTOs, Cairn-wired options (no request)")]
    public Task CairnWired() => JsonSerializer.SerializeAsync(_sink, _items, _cairn);
}
