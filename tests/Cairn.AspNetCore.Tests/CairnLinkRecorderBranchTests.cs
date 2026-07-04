using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Cairn.AspNetCore.Tests;

// Branch-direction coverage for CairnLinkRecorder: format forcing by media type, Vary handling,
// Accept-range specificity edges, once-per-type warnings, and envelope materialization edges.
public class CairnLinkRecorderBranchTests
{
    [Fact]
    public async Task Forcing_a_built_in_media_type_selects_that_format_without_a_registered_formatter()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // WithHypermediaFormat(mediaType) with no custom formatter resolves to the built-in format it names.
        var hal = await client.GetAsync("/forced-hal/1");
        Assert.Equal("application/hal+json", hal.Content.Headers.ContentType?.MediaType);

        var halForms = await client.GetAsync("/forced-hal-forms/1");
        Assert.Equal("application/prs.hal-forms+json", halForms.Content.Headers.ContentType?.MediaType);
        var root = JsonDocument.Parse(await halForms.Content.ReadAsStringAsync()).RootElement;
        Assert.True(root.TryGetProperty("_templates", out _));

        // Forcing plain application/json keeps the Default shape and never relabels the response.
        var json = await client.GetAsync("/forced-json/1");
        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
        Assert.True(JsonDocument.Parse(await json.Content.ReadAsStringAsync()).RootElement.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task An_existing_vary_accept_is_not_duplicated()
    {
        await using var app = await StartAsync(vary: new StringValues("Origin, Accept"));
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/1");

        // The pre-set field already advertises Accept, so the recorder must not append a second one.
        var fields = response.Headers.Vary.SelectMany(v => v.Split(',')).Select(f => f.Trim()).ToList();
        Assert.Single(fields, f => f.Equals("Accept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_vary_wildcard_suppresses_the_accept_append()
    {
        // "Accept-Charset" makes the value contain the substring "Accept" so the field loop runs; the "*"
        // field then already covers every request header, Accept included.
        await using var app = await StartAsync(vary: new StringValues("Accept-Charset, *"));
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/1");

        var fields = response.Headers.Vary.SelectMany(v => v.Split(',')).Select(f => f.Trim()).ToList();
        Assert.DoesNotContain("Accept", fields);
        Assert.Contains("*", fields);
    }

    [Fact]
    public async Task A_vary_field_merely_containing_accept_still_gets_accept_appended()
    {
        // "Accept-Language" contains "Accept" as a substring but is not the Accept field itself.
        await using var app = await StartAsync(vary: new StringValues("Accept-Language"));
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/1");

        var fields = response.Headers.Vary.SelectMany(v => v.Split(',')).Select(f => f.Trim()).ToList();
        Assert.Contains("Accept-Language", fields);
        Assert.Contains("Accept", fields);
    }

    [Fact]
    public async Task A_vary_without_accept_gets_accept_appended_and_null_values_are_skipped()
    {
        await using var app = await StartAsync(vary: new StringValues([null, "Origin"]));
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/1");

        var fields = response.Headers.Vary.SelectMany(v => v.Split(',')).Select(f => f.Trim()).ToList();
        Assert.Contains("Origin", fields);
        Assert.Single(fields, f => f.Equals("Accept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_bare_type_range_matches_every_json_format_and_the_default_wins_the_tie()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/*");

        var response = await client.GetAsync("/orders/1");

        // application/* matches json, hal, and hal-forms with equal specificity; the configured default wins.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Malformed_type_ranges_never_match_a_candidate()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // Each range fails a different structural check against the "application/..." candidates: a type
        // longer than the whole candidate, a type shorter than "application", and one the same length but
        // spelled differently. The exact hal ask must still win untroubled.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/*, applicatio/*, applicatiox/*, application/hal+json");

        var response = await client.GetAsync("/orders/1");

        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Suffix_ranges_without_a_plus_or_with_a_foreign_suffix_match_nothing()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // "application/*x" is not a "+suffix" range, and no emitted format ends in "+xml" — neither range is
        // satisfiable, so the response falls back to the default format.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/*x, application/*+xml");

        var response = await client.GetAsync("/orders/1");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_compute_activity_is_tagged_with_a_custom_formatters_media_type()
    {
        var activities = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CairnDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await using var app = await StartAsync(o => o.AddFormatter(new BranchFormatter()));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.branch+json");

        await client.GetStringAsync("/orders/1");

        // The custom formatter supersedes the built-in format, and the activity names its media type.
        var activity = Assert.Single(activities, a => (string?)a.GetTagItem("cairn.resource_type") == nameof(RecBranchOrder));
        Assert.Equal("application/vnd.branch+json", activity.GetTagItem("cairn.format"));
    }

    [Fact]
    public async Task Bare_strings_null_results_and_null_valued_results_are_passed_through_untouched()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal("hello", await client.GetStringAsync("/raw-string"));

        var nullBody = await client.GetAsync("/raw-null");
        Assert.True(nullBody.IsSuccessStatusCode);

        var nullOk = await client.GetAsync("/ok-null");
        Assert.True(nullOk.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_handler_that_clears_the_endpoint_still_records_links()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // With no endpoint on the context, format forcing and per-route pagination metadata are simply absent.
        var single = JsonDocument.Parse(await client.GetStringAsync("/no-endpoint")).RootElement;
        Assert.True(single.TryGetProperty("_links", out _));

        var paged = JsonDocument.Parse(await client.GetStringAsync("/no-endpoint-paged")).RootElement;
        Assert.True(paged.GetProperty("_links").TryGetProperty("self", out _));

        var cursored = JsonDocument.Parse(await client.GetStringAsync("/no-endpoint-cursor")).RootElement;
        Assert.True(cursored.GetProperty("_links").TryGetProperty("next", out _));
    }

    [Fact]
    public async Task The_global_cursor_link_builder_is_used_when_no_route_override_exists()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.CursorLink = (request, cursor) => $"https://global.test/feed?after={cursor}");

        await using var app = builder.Build();
        app.MapGet("/feed", () => TypedResults.Ok(new CursorPage<int>([1, 2], Next: "NXT"))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/feed")).RootElement.GetProperty("_links");

        Assert.Equal("https://global.test/feed?after=NXT", links.GetProperty("next").GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_deferred_sequence_in_an_immutable_result_warns_once_per_element_type()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs: logs);
        using var client = app.GetTestClient();

        await client.GetAsync("/deferred-ok");
        await client.GetAsync("/deferred-ok");

        Assert.Single(logs.Messages, m => m.Contains("deferred sequence", StringComparison.Ordinal) && m.Contains("immutable result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_async_stream_warns_once_per_element_type()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs: logs);
        using var client = app.GetTestClient();

        await client.GetAsync("/async-stream");
        await client.GetAsync("/async-stream");

        Assert.Single(logs.Messages, m => m.Contains("IAsyncEnumerable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Materialized_and_envelope_shapes_in_an_immutable_result_do_not_warn_deferred()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs: logs);
        using var client = app.GetTestClient();

        // A HashSet<T> exposes only the generic collection interfaces, so materialization is detected through
        // them; strings and paged/cursor envelopes that are themselves enumerable are exempt by shape.
        var set = JsonDocument.Parse(await client.GetStringAsync("/set-ok")).RootElement;
        Assert.True(set[0].GetProperty("_links").TryGetProperty("self", out _));

        await client.GetStringAsync("/string-ok");
        await client.GetStringAsync("/enumerable-paged-ok");
        await client.GetStringAsync("/enumerable-cursor-ok");

        Assert.DoesNotContain(logs.Messages, m => m.Contains("deferred sequence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_paging_envelope_that_is_itself_an_enumerable_serializes_as_a_bare_array_without_links()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddPaging<DeferredEnvelope>(e => new PagedView(e.Names, 1, 10, 2)));

        await using var app = builder.Build();
        app.MapGet("/denv", () => TypedResults.Ok(new DeferredEnvelope())).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        // The envelope type is itself a deferred IEnumerable, so it serializes through the enumerable's custom
        // JSON converter: Cairn's object-contract property injection never runs, the body is a bare array, and
        // the recorder reports that the computed hypermedia cannot be emitted onto a non-object contract.
        var root = JsonDocument.Parse(await client.GetStringAsync("/denv")).RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
        Assert.Single(logs.Messages, m => m.Contains("cannot be emitted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_envelope_with_no_settable_items_property_warns_once_about_deferred_items()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/init-env", () => TypedResults.Ok(new InitOnlyItemsPage())).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.GetAsync("/init-env");
        await client.GetAsync("/init-env");

        // The items property is init-only, so the deferred sequence cannot be buffered back into it.
        Assert.Single(logs.Messages, m => m.Contains("no settable property", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configured_problem_and_value_type_responses_do_not_warn_unconfigured()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(logs: logs);
        using var client = app.GetTestClient();

        // A ProblemDetails body, a boxed value type, and a configured type whose links are all gated off:
        // none of them is a missing registration, so none should warn.
        await client.GetStringAsync("/problem-ok");
        await client.GetStringAsync("/int-ok");
        await client.GetStringAsync("/gated-empty");

        Assert.DoesNotContain(logs.Messages, m => m.Contains("no link configuration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applying_WithLinks_twice_records_and_emits_once()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new RecBranchOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id))).WithName("RecGetOrder").WithLinks().WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("RecCancel");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/orders/5")).RootElement;

        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task A_curie_used_only_by_affordance_names_still_gets_a_curies_array()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddCurie("doc", "https://docs.test/rels/{rel}");
            o.AddLinks(new CuriedActionsLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/curied/{id:int}", (int id) => TypedResults.Ok(new CuriedActionsDoc(id))).WithName("CuriedGet").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/curied/1")).RootElement;

        // The config declares no links at all, so the curies array is the only _links member.
        var curies = root.GetProperty("_links").GetProperty("curies");
        Assert.Equal(JsonValueKind.Array, curies.ValueKind);
        Assert.Equal("doc", curies[0].GetProperty("name").GetString());
        Assert.True(root.GetProperty("_actions").TryGetProperty("doc:approve", out _));
    }

    [Fact]
    public async Task The_content_type_swap_leaves_non_json_missing_and_unparseable_content_types_alone()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.Hal;
            o.AddLinks(new RecBranchOrderLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id))).WithName("RecGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("RecCancel");

        // Each outer filter lets Cairn record the value, then substitutes a response whose content type the
        // OnStarting swap must not touch: an explicit text type, no type at all, and an unparseable one.
        app.MapGet("/swap-text/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id)))
            .AddEndpointFilter(async (context, next) =>
            {
                _ = await next(context);
                return Results.Text("plain", "text/plain");
            })
            .WithLinks();
        app.MapGet("/swap-empty/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id)))
            .AddEndpointFilter(async (context, next) =>
            {
                _ = await next(context);
                return TypedResults.StatusCode(StatusCodes.Status200OK);
            })
            .WithLinks();
        app.MapGet("/swap-bogus/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id)))
            .AddEndpointFilter(async (context, next) =>
            {
                _ = await next(context);
                context.HttpContext.Response.ContentType = "not a media type";
                return TypedResults.StatusCode(StatusCodes.Status200OK);
            })
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var text = await client.GetAsync("/swap-text/1");
        Assert.Equal("text/plain", text.Content.Headers.ContentType?.MediaType);

        var empty = await client.GetAsync("/swap-empty/1");
        Assert.True(empty.IsSuccessStatusCode);
        Assert.Null(empty.Content.Headers.ContentType);

        var bogus = await client.GetAsync("/swap-bogus/1");
        Assert.True(bogus.IsSuccessStatusCode);

        // The ordinary path still relabels plain application/json to hal.
        var normal = await client.GetAsync("/orders/1");
        Assert.Equal("application/hal+json", normal.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<WebApplication> StartAsync(
        Action<CairnOptions>? configure = null,
        StringValues vary = default,
        CapturingLoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new RecBranchOrderLinks());
            o.AddLinks(new GatedEmptyLinks());
            configure?.Invoke(o);
        });

        var app = builder.Build();
        if (vary.Count > 0)
        {
            app.Use(async (context, next) =>
            {
                context.Response.Headers.Vary = vary;
                await next(context);
            });
        }

        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id))).WithName("RecGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("RecCancel");

        app.MapGet("/forced-hal/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id)))
            .WithLinks().WithHypermediaFormat("application/hal+json");
        app.MapGet("/forced-hal-forms/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id)))
            .WithLinks().WithHypermediaFormat("application/prs.hal-forms+json");
        app.MapGet("/forced-json/{id:int}", (int id) => TypedResults.Ok(new RecBranchOrder(id)))
            .WithLinks().WithHypermediaFormat("application/json");

        app.MapGet("/raw-string", () => "hello").WithLinks();
        app.MapGet("/raw-null", object? () => null).WithLinks();
        app.MapGet("/ok-null", () => TypedResults.Ok<RecBranchOrder>(null)).WithLinks();

        app.MapGet("/no-endpoint", (HttpContext http) =>
        {
            http.SetEndpoint(null);
            return TypedResults.Ok(new RecBranchOrder(1));
        }).WithLinks();
        app.MapGet("/no-endpoint-paged", (HttpContext http) =>
        {
            http.SetEndpoint(null);
            return TypedResults.Ok(new PagedResource<int>([1], Page: 1, PageSize: 10, TotalCount: 1));
        }).WithLinks();
        app.MapGet("/no-endpoint-cursor", (HttpContext http) =>
        {
            http.SetEndpoint(null);
            return TypedResults.Ok(new CursorPage<int>([1], Next: "N"));
        }).WithLinks();

        app.MapGet("/deferred-ok", () => TypedResults.Ok(DeferredOrders())).WithLinks();
        app.MapGet("/async-stream", () => StreamOrders()).WithLinks();
        app.MapGet("/set-ok", () => TypedResults.Ok(new HashSet<RecBranchOrder> { new(1), new(2) })).WithLinks();
        app.MapGet("/string-ok", () => TypedResults.Ok("just text")).WithLinks();
        app.MapGet("/enumerable-paged-ok", () => TypedResults.Ok(new EnumerablePage())).WithLinks();
        app.MapGet("/enumerable-cursor-ok", () => TypedResults.Ok(new EnumerableCursorPage())).WithLinks();

        app.MapGet("/problem-ok", () => TypedResults.Ok(new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = 200 })).WithLinks();
        app.MapGet("/int-ok", () => TypedResults.Ok(42)).WithLinks();
        app.MapGet("/gated-empty", () => TypedResults.Ok(new GatedEmptyDoc(1))).WithLinks();

        await app.StartAsync();
        return app;
    }

    private static IEnumerable<RecBranchOrder> DeferredOrders()
    {
        yield return new RecBranchOrder(1);
    }

    private static async IAsyncEnumerable<RecBranchOrder> StreamOrders()
    {
        await Task.Yield();
        yield return new RecBranchOrder(1);
    }

    private sealed record RecBranchOrder(int Id);

    // A minimal custom format, negotiated by its own media type, used to prove the compute activity is
    // tagged with the formatter's media type when a registered format supersedes the built-in set.
    private sealed class BranchFormatter : IHypermediaFormatter
    {
        public string MediaType => "application/vnd.branch+json";

        public IReadOnlyList<HypermediaFormatProperty> Properties { get; } =
        [
            new("links", document => document.Links.Count == 0
                ? null
                : document.Links.Select(link => new Dictionary<string, object>
                {
                    ["rel"] = new[] { link.Relation.Value },
                    ["href"] = link.Href,
                }).ToList()),
        ];
    }

    private sealed record GatedEmptyDoc(int Id);

    private sealed record CuriedActionsDoc(int Id);

    // A registered paging envelope that is itself a deferred IEnumerable (no collection interfaces).
    private sealed class DeferredEnvelope : IEnumerable<string>
    {
        public IReadOnlyList<string> Names { get; } = ["a", "b"];

        public IEnumerator<string> GetEnumerator()
        {
            foreach (var name in Names)
            {
                yield return name;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // An offset envelope whose deferred items sit behind an init-only property, so they cannot be buffered back.
    private sealed class InitOnlyItemsPage : IPagedResource
    {
        public IEnumerable Items { get; init; } = Enumerable.Range(1, 2).Select(i => new RecBranchOrder(i));

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 2;

        public int TotalPages => 1;
    }

    // An envelope that is both an offset page and an enumerable of its own items.
    private sealed class EnumerablePage : IPagedResource, IEnumerable<int>
    {
        public IEnumerable Items => new[] { 1, 2 };

        public int Page => 1;

        public int PageSize => 10;

        public int TotalCount => 2;

        public int TotalPages => 1;

        public IEnumerator<int> GetEnumerator()
        {
            yield return 1;
            yield return 2;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // A cursor envelope that is also an enumerable of its own items.
    private sealed class EnumerableCursorPage : ICursorPagedResource, IEnumerable<int>
    {
        public IEnumerable Items => new[] { 1 };

        public string? Next => "N";

        public string? Prev => null;

        public IEnumerator<int> GetEnumerator()
        {
            yield return 1;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecBranchOrderLinks : LinkConfig<RecBranchOrder>
    {
        public override void Configure(ILinkBuilder<RecBranchOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("RecGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("RecCancel", new { id = order.Id })).Post();
        }
    }

    // Every declared link is gated off, so the link set is empty even though a config exists.
    private sealed class GatedEmptyLinks : LinkConfig<GatedEmptyDoc>
    {
        public override void Configure(ILinkBuilder<GatedEmptyDoc> builder)
            => builder.Self(doc => LinkTarget.Route("RecGetOrder", new { id = doc.Id })).When(_ => false);
    }

    // Affordances only — no links — with curie-prefixed names, to force the curies array to create _links.
    private sealed class CuriedActionsLinks : LinkConfig<CuriedActionsDoc>
    {
        public override void Configure(ILinkBuilder<CuriedActionsDoc> builder)
        {
            builder.Affordance("doc:approve", doc => LinkTarget.Route("CuriedGet", new { id = doc.Id })).Post();
            builder.Affordance("doc:reject", doc => LinkTarget.Route("CuriedGet", new { id = doc.Id })).Post();
        }
    }
}
