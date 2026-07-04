using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// RFC 6570 corners beyond the mainline operators: literal passthrough of malformed expressions, the
// label/path/query-continuation operators, non-exploded maps, and the alternate shapes a caller can
// supply variables in (read-only dictionaries, pair sequences, wire-format scalars).
public class CairnUriTemplateEdgeTests
{
    [Fact]
    public async Task A_template_without_expressions_expands_to_itself()
    {
        var (client, handler) = NewRecordingClient();

        // A server may mark a link templated even when every expression was already resolved away.
        await client.FollowAsync<JsonElement>(new Link("item", "/plain/path", templated: true), (object?)null);

        Assert.Equal("/plain/path", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task An_unclosed_expression_passes_through_verbatim()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("item", "/items{unclosed", templated: true), new { unclosed = "x" });

        Assert.Contains("/items{unclosed", Uri.UnescapeDataString(handler.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task An_empty_expression_expands_to_nothing()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("item", "/items{}/list", templated: true), (object?)null);

        Assert.Equal("/items/list", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Label_and_path_segment_operators_expand()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(
            new Link("file", "/file{.ext}{/section}", templated: true),
            new { ext = "json", section = "docs" });

        Assert.Equal("/file.json/docs", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Query_continuation_appends_to_an_existing_query()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("search", "/s?fixed=1{&page}", templated: true), new { page = 2 });

        Assert.Equal("?fixed=1&page=2", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task A_map_without_explode_expands_to_named_key_value_pairs()
    {
        var (client, handler) = NewRecordingClient();

        var variables = new { filter = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" } };
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?filter}", templated: true), variables);

        // RFC 6570 §3.2.8: without explode the pairs join under the single variable name.
        Assert.Equal("?filter=a,1,b,2", Uri.UnescapeDataString(handler.RequestUri!.Query));
    }

    [Fact]
    public async Task A_lone_surrogate_encodes_as_the_replacement_character()
    {
        var (client, handler) = NewRecordingClient();

        // A lone high surrogate has no code point; it must become U+FFFD rather than corrupt the URI.
        await client.FollowAsync<JsonElement>(new Link("file", "/files/{+name}", templated: true), new { name = "x\uD83D" });

        Assert.Contains("/files/x%EF%BF%BD", handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_pair_sequence_value_without_IDictionary_is_treated_as_a_map()
    {
        var (client, handler) = NewRecordingClient();

        // KeyValuePair<string,·>[] implements IEnumerable<KeyValuePair<string,·>> but not IDictionary,
        // exercising the reflective Key/Value read (the FrozenDictionary-style shape).
        var variables = new { m = new[] { KeyValuePair.Create("a", "1"), KeyValuePair.Create("b", "2") } };
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?m*}", templated: true), variables);

        Assert.Equal("?a=1&b=2", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task Variables_supplied_as_a_pair_sequence_expand()
    {
        var (client, handler) = NewRecordingClient();

        var variables = new[] { new KeyValuePair<string, object?>("page", "2") };
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?page}", templated: true), variables);

        Assert.Equal("?page=2", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task Variables_supplied_as_a_read_only_dictionary_expand_without_reflection()
    {
        var (client, handler) = NewRecordingClient();

        var variables = new Dictionary<string, object?> { ["page"] = 2 };
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?page}", templated: true), variables);

        Assert.Equal("?page=2", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task Date_and_time_scalars_expand_in_round_trip_format()
    {
        var (client, handler) = NewRecordingClient();

        var variables = new
        {
            at = new DateTimeOffset(2026, 7, 3, 8, 30, 0, TimeSpan.Zero),
            day = new DateOnly(2026, 7, 3),
            time = new TimeOnly(8, 30),
        };
        await client.FollowAsync<JsonElement>(new Link("search", "/s{?at,day,time}", templated: true), variables);

        Assert.Equal(
            "?at=2026-07-03T08:30:00.0000000+00:00&day=2026-07-03&time=08:30:00.0000000",
            Uri.UnescapeDataString(handler.RequestUri!.Query));
    }

    [Fact]
    public async Task A_value_without_invariant_formatting_expands_via_ToString()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("search", "/s{?host}", templated: true), new { host = new HostName("example") });

        Assert.Equal("?host=example", handler.RequestUri!.Query);
    }

    private sealed class HostName(string name)
    {
        public override string ToString() => name;
    }

    private static (CairnClient Client, RecordingHandler Handler) NewRecordingClient()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return (new CairnClient(http), handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
