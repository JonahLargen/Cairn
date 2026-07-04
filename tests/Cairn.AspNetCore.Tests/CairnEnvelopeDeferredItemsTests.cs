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
    public async Task A_throwing_getter_is_skipped_when_locating_the_deferred_items_property()
    {
        var projections = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        await using var app = builder.Build();
        app.MapGet("/dt", () => TypedResults.Ok(new ThrowingGetterPage
        {
            Items = new[] { 1, 2 }.Select(id =>
            {
                Interlocked.Increment(ref projections);
                return new DeferredItem(id);
            }),
        })).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/dt")).RootElement;

        // The booby-trapped property was skipped and the real items property still got the buffer:
        // one enumeration, links intact.
        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.EndsWith("/di/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal(2, Volatile.Read(ref projections));
    }

    [Fact]
    public async Task A_non_generic_deferred_sequence_buffers_into_object_elements()
    {
        var enumerations = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        await using var app = builder.Build();
        app.MapGet("/dng", () => new NonGenericSequence(() => Interlocked.Increment(ref enumerations))).WithLinks();
        app.MapGet("/di2/{id:int}", (int id) => TypedResults.Ok(new DeferredItem(id)));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/dng")).RootElement;

        // Only the non-generic IEnumerable is available, so the buffer falls back to object elements —
        // items still serialize by their runtime contracts and carry their links.
        Assert.Equal(2, root.GetArrayLength());
        Assert.EndsWith("/di/1", root[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal(1, Volatile.Read(ref enumerations));   // buffered once, serialized from the buffer
    }

    [Fact]
    public async Task Nested_deferred_envelopes_each_buffer_their_items_independently()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        await using var app = builder.Build();
        app.MapGet("/nested", () =>
        {
            // An outer page whose deferred items yield an inner page that also defers its items: both
            // sequences are buffered in the one request, so each keeps its own correlated instances.
            var inner = new DeferredPage
            {
                Items = new[] { 7 }.Select(id => new DeferredItem(id)),
            };
            var outer = new NestedOuterPage
            {
                Items = new object[] { inner }.Select(x => x),
            };
            return TypedResults.Ok(outer);
        }).WithLinks();
        app.MapGet("/di/{id:int}", (int id) => TypedResults.Ok(new DeferredItem(id))).WithName("DefGetItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/nested")).RootElement;

        // The outer envelope carries its pagination links and exactly one nested page...
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
        var outerItems = root.GetProperty("items");
        Assert.Equal(1, outerItems.GetArrayLength());

        // ...whose own deferred items were buffered separately and kept their links.
        var innerItems = outerItems[0].GetProperty("items");
        Assert.Equal(1, innerItems.GetArrayLength());
        Assert.EndsWith("/di/7", innerItems[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_string_valued_items_property_is_left_alone()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        StringItemsPage? envelope = null;
        await using var app = builder.Build();
        app.MapGet("/ds", () => TypedResults.Ok(envelope = new StringItemsPage())).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/ds")).RootElement;

        // A string is IEnumerable, but it must never be "buffered" into a List<char> and written back.
        Assert.Equal("abc", root.GetProperty("items").GetString());
        Assert.Equal("abc", envelope!.Items);
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task An_init_only_envelope_keeps_its_item_links_and_is_never_mutated()
    {
        var projections = 0;
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        InitOnlyPage? envelope = null;
        var deferred = new[] { 1, 2 }.Select(id =>
        {
            Interlocked.Increment(ref projections);
            return new DeferredItem(id);
        });

        await using var app = builder.Build();
        app.MapGet("/dio", () => TypedResults.Ok(envelope = new InitOnlyPage { Items = deferred })).WithLinks();
        app.MapGet("/di/{id:int}", (int id) => TypedResults.Ok(new DeferredItem(id))).WithName("DefGetItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/dio")).RootElement;

        // The buffer is substituted at serialization rather than written back through the init-only property,
        // so the items keep their links and the deferred sequence is enumerated exactly once...
        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.EndsWith("/di/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("/di/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
        Assert.Equal(2, Volatile.Read(ref projections));

        // ...while the declared immutability contract holds: reflection never rewrites the init-only property,
        // and nothing warns, because the correlation between compute and serialization was never broken.
        Assert.Same(deferred, envelope!.Items);
        Assert.DoesNotContain(logs.Messages, m => m.Contains("no stable readable property", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_shared_envelope_with_deferred_items_is_not_mutated_across_requests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DeferredItemLinks()));

        // One envelope instance, reused for every request, as an app returning a cached/singleton envelope would.
        var deferred = new[] { 1, 2 }.Select(id => new DeferredItem(id));
        var shared = new DeferredPage { Items = deferred };

        await using var app = builder.Build();
        app.MapGet("/shared", () => TypedResults.Ok(shared)).WithLinks();
        app.MapGet("/di/{id:int}", (int id) => TypedResults.Ok(new DeferredItem(id))).WithName("DefGetItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var first = JsonDocument.Parse(await client.GetStringAsync("/shared")).RootElement;
        var second = JsonDocument.Parse(await client.GetStringAsync("/shared")).RootElement;

        // Both requests against the same instance serve correlated item links...
        foreach (var root in new[] { first, second })
        {
            var items = root.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            Assert.EndsWith("/di/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
            Assert.EndsWith("/di/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        }

        // ...and the shared envelope was never rewritten behind the app's back: its items property still holds
        // the original deferred sequence, not a buffered list swapped in by the first request.
        Assert.Same(deferred, shared.Items);
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

    // An immutable envelope: the items property only has an init accessor. The recorder must not write a buffer
    // back through it, yet the items still keep their links — the buffer is substituted at serialization instead.
    private sealed class InitOnlyPage : IPagedResource
    {
        public required IEnumerable<DeferredItem> Items { get; init; }

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 2;

        public int TotalPages => 1;

        IEnumerable IPagedResource.Items => Items;
    }

    // An envelope whose first settable object-typed property throws from its getter: the recorder's
    // scan for the items property must skip it (a throwing getter can't be the items source), not abort.
    private sealed class ThrowingGetterPage : IPagedResource
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public object? Trap { get => throw new InvalidOperationException("inspected too eagerly"); set => _ = value; }

        public required IEnumerable<DeferredItem> Items { get; set; }

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 2;

        public int TotalPages => 1;

        IEnumerable IPagedResource.Items => Items;
    }

    // A sequence exposing only non-generic IEnumerable: no element type to recover, deferred until enumerated.
    private sealed class NonGenericSequence(Action onEnumerate) : IEnumerable
    {
        public IEnumerator GetEnumerator()
        {
            onEnumerate();
            yield return new DeferredItem(1);
            yield return new DeferredItem(2);
        }
    }

    // A pathological envelope whose items are a string — IEnumerable, yet not a sequence to buffer.
    private sealed class StringItemsPage : IPagedResource
    {
        public string Items { get; set; } = "abc";

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 1;

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

    // An offset envelope whose deferred items are themselves paging envelopes, so materializing them buffers a
    // second deferred sequence within the same request.
    private sealed class NestedOuterPage : IPagedResource
    {
        public required IEnumerable<object> Items { get; set; }

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 1;

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
