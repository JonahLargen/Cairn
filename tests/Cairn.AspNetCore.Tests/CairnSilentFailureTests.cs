using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

/// <summary>
/// Guards the failure modes where hypermedia used to vanish with no links and no error: bare (unwrapped)
/// handler returns, deferred sequences, async streams, and unregistered resource types.
/// </summary>
public class CairnSilentFailureTests
{
    [Fact]
    public async Task Bare_dto_return_gets_links()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new UriLinks<BareOrder>(o => $"/bare/{o.Id}")));

        await using var app = builder.Build();
        app.MapGet("/bare/{id:int}", (int id) => new BareOrder(id)).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = await GetJsonAsync(client, "/bare/7");

        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.Equal("/bare/7", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Bare_deferred_sequence_is_buffered_enumerated_once_and_linked()
    {
        var source = new CountingEnumerable<SeqOrder>(new[] { 1, 2 }.Select(i => new SeqOrder(i)));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new UriLinks<SeqOrder>(o => $"/seq/{o.Id}")));

        await using var app = builder.Build();
        app.MapGet("/seq", IEnumerable<SeqOrder> () => source).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var items = (await GetJsonAsync(client, "/seq")).EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("/seq/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("/seq/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal(1, source.Enumerations);
    }

    [Fact]
    public async Task Deferred_projection_inside_a_typed_result_warns_that_links_were_never_emitted()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new UriLinks<MissOrder>(o => $"/miss/{o.Id}")));

        await using var app = builder.Build();

        // The projection runs once in the recorder and again in the serializer, yielding new instances the
        // second time — the immutable Ok<T> cannot carry the buffer forward, so the links are lost.
        app.MapGet("/miss", () => TypedResults.Ok(new[] { 1 }.Select(i => new MissOrder(i)))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/miss");
        await client.GetStringAsync("/miss");   // second request must not warn again

        Assert.DoesNotContain("_links", body);
        Assert.Single(logs.Messages, m => m.Contains("MissOrder", StringComparison.Ordinal) && m.Contains("never emitted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identity_preserving_deferred_sequence_inside_a_typed_result_still_links()
    {
        var orders = new List<KeepOrder> { new(1), new(2) };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new UriLinks<KeepOrder>(o => $"/keep/{o.Id}")));

        await using var app = builder.Build();
        app.MapGet("/keep", () => TypedResults.Ok(orders.Where(_ => true))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var items = (await GetJsonAsync(client, "/keep")).EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("/keep/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Async_stream_serializes_without_links_and_warns_once()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new UriLinks<AsyncOrder>(o => $"/stream/{o.Id}")));

        await using var app = builder.Build();
        app.MapGet("/stream", () => StreamOrders()).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/stream");
        await client.GetStringAsync("/stream");   // second request must not warn again

        Assert.Contains("\"id\":1", body);
        Assert.DoesNotContain("_links", body);
        Assert.Single(logs.Messages, m => m.Contains("IAsyncEnumerable", StringComparison.Ordinal) && m.Contains("AsyncOrder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Derived_dto_uses_the_base_type_config()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new UriLinks<InheritOrder>(o => $"/inherit/{o.Id}")));

        await using var app = builder.Build();
        app.MapGet("/inherit/{id:int}", (int id) => TypedResults.Ok(new RushInheritOrder(id))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = await GetJsonAsync(client, "/inherit/5");

        Assert.Equal("/inherit/5", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Unconfigured_resource_type_warns_once()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn();   // no config registered for LonelyOrder

        await using var app = builder.Build();
        app.MapGet("/lonely/{id:int}", (int id) => TypedResults.Ok(new LonelyOrder(id))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/lonely/1");
        await client.GetStringAsync("/lonely/2");   // second request must not warn again

        Assert.DoesNotContain("_links", body);
        Assert.Single(logs.Messages, m => m.Contains("no link configuration", StringComparison.Ordinal) && m.Contains("LonelyOrder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mvc_deferred_sequence_is_buffered_and_linked()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(DeferredMvcController).Assembly);
        builder.Services.AddCairn(o => o.AddLinks(new UriLinks<MvcSeqOrder>(o => $"/mvc-seq/{o.Id}")));

        await using var app = builder.Build();
        app.MapControllers();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var items = (await GetJsonAsync(client, "/mvc-seq")).EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("/mvc-seq/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("/mvc-seq/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal(1, DeferredMvcController.Source.Enumerations);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var json = await client.GetStringAsync(path);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async IAsyncEnumerable<AsyncOrder> StreamOrders()
    {
        await Task.Yield();
        yield return new AsyncOrder(1);
    }

    public sealed record BareOrder(int Id);

    public sealed record SeqOrder(int Id);

    public sealed record MissOrder(int Id);

    public sealed record KeepOrder(int Id);

    public sealed record AsyncOrder(int Id);

    public record InheritOrder(int Id);

    public sealed record RushInheritOrder(int Id) : InheritOrder(Id);

    public sealed record LonelyOrder(int Id);

    public sealed record MvcSeqOrder(int Id);

    private sealed class UriLinks<T>(Func<T, string> self) : LinkConfig<T>
    {
        public override void Configure(ILinkBuilder<T> builder)
            => builder.Self(r => LinkTarget.Uri(self(r)));
    }

    public sealed class CountingEnumerable<T>(IEnumerable<T> inner) : IEnumerable<T>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }
}

[ApiController]
[Route("mvc-seq")]
public sealed class DeferredMvcController : ControllerBase
{
    public static readonly CairnSilentFailureTests.CountingEnumerable<CairnSilentFailureTests.MvcSeqOrder> Source =
        new(new[] { 1, 2 }.Select(i => new CairnSilentFailureTests.MvcSeqOrder(i)));

    [HttpGet]
    [CairnLinks]
    public IEnumerable<CairnSilentFailureTests.MvcSeqOrder> List() => Source;
}
