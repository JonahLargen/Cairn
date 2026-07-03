using System.Collections;
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
        var completion = new ResponseCompletion();
        completion.Use(app);
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

        // Wait for the response to fully complete (the barrier fires after Cairn's emit-miss callback) before
        // asserting the diagnostic did NOT fire — otherwise its absence could just mean the callback is pending.
        await completion.WaitAsync();
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

    [Fact]
    public async Task An_init_only_envelope_is_never_mutated_and_warns_about_its_deferred_items()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        InitOnlyPage? envelope = null;
        var deferred = new[] { 1, 2 }.Select(id => new DeferredItem(id));

        await using var app = builder.Build();
        app.MapGet("/dio", () => TypedResults.Ok(envelope = new InitOnlyPage { Items = deferred })).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/dio")).RootElement;

        // The response still works and carries the pagination links...
        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));

        // ...but the init-only property was NOT rewritten through reflection: the declared immutability
        // contract holds, and the deferred-items warning explains what that costs.
        Assert.Same(deferred, envelope!.Items);
        Assert.Contains(logs.Messages, m => m.Contains("no settable property", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deferred_sequence_inside_an_immutable_result_warns_while_the_request_runs()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        await using var app = builder.Build();
        app.MapGet("/dr", () => TypedResults.Ok(new[] { 1, 2 }.Select(id => new DeferredItem(id)))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/dr")).RootElement;

        Assert.Equal(2, root.GetArrayLength());

        // The eager diagnostic names the real problem (the sequence cannot be buffered into the immutable
        // result) instead of leaving only the post-response emit-miss trail.
        Assert.Contains(logs.Messages, m => m.Contains("immutable result", StringComparison.Ordinal));
    }

    private sealed record DeferredItem(int Id);

    // An immutable envelope: the items property only has an init accessor, so the recorder must not write
    // a buffer back through it.
    private sealed class InitOnlyPage : IPagedResource
    {
        public required IEnumerable<DeferredItem> Items { get; init; }

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 2;

        public int TotalPages => 1;

        IEnumerable IPagedResource.Items => Items;
    }

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
}
