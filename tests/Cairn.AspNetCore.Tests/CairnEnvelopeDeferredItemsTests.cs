using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

public class CairnEnvelopeDeferredItemsTests
{
    [Fact]
    public async Task A_paged_envelope_with_deferred_items_is_enumerated_once_and_keeps_item_links()
    {
        var projections = 0;
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        await using var app = builder.Build();
        app.MapGet("/dp", () =>
        {
            // A deferred LINQ projection: without buffering, the recorder and the serializer would each run
            // it, doubling the query and producing fresh (link-less) instances for the second pass.
            var page = new DeferredPage
            {
                Items = new[] { 1, 2 }.Select(id =>
                {
                    Interlocked.Increment(ref projections);
                    return new DeferredItem(id);
                }),
            };
            return TypedResults.Ok(page);
        }).WithLinks();
        app.MapGet("/di/{id:int}", (int id) => TypedResults.Ok(new DeferredItem(id))).WithName("DefGetItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/dp")).RootElement;

        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.EndsWith("/di/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("/di/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());

        // The envelope still carries pagination links.
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));

        Assert.Equal(2, Volatile.Read(ref projections));   // each element projected exactly once
        Assert.DoesNotContain(logs.Messages, m => m.Contains("never emitted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cursor_envelope_with_deferred_items_is_enumerated_once_and_keeps_item_links()
    {
        var projections = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        await using var app = builder.Build();
        app.MapGet("/dc", () => TypedResults.Ok(new DeferredCursorPage
        {
            Items = new[] { 7 }.Select(id =>
            {
                Interlocked.Increment(ref projections);
                return new DeferredItem(id);
            }),
        })).WithLinks();
        app.MapGet("/dci/{id:int}", (int id) => TypedResults.Ok(new { id })).WithName("DefGetCursorItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/dc")).RootElement;

        var items = root.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.EndsWith("/di/7", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal(1, Volatile.Read(ref projections));
    }

    private sealed record DeferredItem(int Id);

    // A mutable envelope, as an app would write one over a repository query.
    private sealed class DeferredPage : IPagedResource
    {
        public required IEnumerable<DeferredItem> Items { get; set; }

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 2;

        public int TotalPages => 1;

        IEnumerable IPagedResource.Items => Items;
    }

    private sealed class DeferredCursorPage : ICursorPagedResource
    {
        public required IEnumerable<DeferredItem> Items { get; set; }

        public string? Next => null;

        public string? Prev => null;

        IEnumerable ICursorPagedResource.Items => Items;
    }

    private sealed class DeferredItemLinks : LinkConfig<DeferredItem>
    {
        public override void Configure(ILinkBuilder<DeferredItem> builder)
            => builder.Self(item => LinkTarget.Uri($"/di/{item.Id}"));
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
