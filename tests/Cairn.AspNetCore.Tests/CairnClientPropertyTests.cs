using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Cairn.AspNetCore.Tests;

// Property-based tests: the client's HAL parser and RFC 6570 template expansion are driven with
// randomized documents and variable values, checking invariants that must hold for any input.
[Properties(Arbitrary = [typeof(ClientPropertyArbitraries)])]
public class CairnClientPropertyTests
{
    [Property]
    public bool Well_formed_links_round_trip_through_the_parser(HalLinkEntries entries)
    {
        var links = entries.Items.ToDictionary(e => e.Rel, e => (object)new { href = e.Href, title = e.Title });
        var resource = Get(JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = 1, ["_links"] = links }));

        return entries.Items.All(e =>
            resource.HasLink(e.Rel)
            && resource.Links[e.Rel].Href == e.Href
            && resource.Links[e.Rel].Title == e.Title);
    }

    [Property]
    public bool Query_template_expansion_round_trips_any_variable_value(TemplateValue value)
    {
        Uri? requested = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return JsonResponse("{}");
        }))
        {
            BaseAddress = new Uri("http://cairn.test"),
        };
        var link = new Link("search", "/search{?q}", templated: true);

        var result = Run(() => new CairnClient(http).FollowAsync<JsonElement>(link, new { q = value.Value }));

        var query = requested!.Query;
        var encoded = query["?q=".Length..];
        return result.IsSuccess
            && query.StartsWith("?q=", StringComparison.Ordinal)
            && encoded.All(char.IsAscii)
            && Uri.UnescapeDataString(encoded) == value.Value;
    }

    [Property]
    public bool Parser_tolerates_arbitrarily_shaped_link_values(HalLinkJunk junk)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["_links"] = junk.Values,
        });

        var resource = Get(body);

        // Whatever the shapes were, parsing succeeded and every surfaced link carries a href.
        return resource.Links.Values.All(l => !string.IsNullOrEmpty(l.Href));
    }

    private static Resource<JsonElement> Get(string body)
    {
        using var http = new HttpClient(new StubHandler(_ => JsonResponse(body)))
        {
            BaseAddress = new Uri("http://cairn.test"),
        };

        return Run(() => new CairnClient(http).GetAsync<JsonElement>("/x")).EnsureSuccess();
    }

    // Properties are synchronous; run client calls on the pool so no test synchronization context is captured.
    private static T Run<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}

/// <summary>A set of well-formed HAL link entries with relations distinct under case-insensitive comparison.</summary>
public sealed record HalLinkEntries(IReadOnlyList<(string Rel, string Href, string? Title)> Items);

/// <summary>A <c>_links</c> object whose values have arbitrary JSON shapes (scalars, nulls, objects, arrays).</summary>
public sealed record HalLinkJunk(Dictionary<string, object?> Values);

/// <summary>An arbitrary URI template variable value: any characters except lone surrogates.</summary>
public sealed record TemplateValue(string Value)
{
    public override string ToString() => $"\"{Value}\"";
}

/// <summary>Generators for client-side property tests.</summary>
public static class ClientPropertyArbitraries
{
    private static Gen<char> PrintableChar => Gen.OneOf(
        Gen.Choose(33, 126).Select(i => (char)i),
        Gen.Elements('é', 'Ø', 'ß', 'λ', 'Д', '日', '–', ' '));

    private static Gen<string> Text(int minLength, int maxLength)
        => from length in Gen.Choose(minLength, maxLength)
           from chars in PrintableChar.ArrayOf(length)
           select new string(chars);

    // "curies" is HAL-reserved and handled separately by the client; keep it out of random relations.
    private static Gen<string> Rel
        => Text(1, 16).Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals("curies", StringComparison.OrdinalIgnoreCase));

    private static Gen<string> Href => Text(1, 24).Where(s => !string.IsNullOrWhiteSpace(s));

    public static Arbitrary<HalLinkEntries> LinkEntries()
        => (from count in Gen.Choose(1, 8)
            from items in (from rel in Rel
                           from href in Href
                           from hasTitle in Gen.Elements(true, false)
                           from title in Text(0, 12)
                           select (Rel: rel, Href: href, Title: hasTitle ? title : null)).ArrayOf(count)
            select new HalLinkEntries(
                items.DistinctBy(i => i.Rel, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArbitrary();

    public static Arbitrary<HalLinkJunk> LinkJunk()
        => (from count in Gen.Choose(0, 8)
            from pairs in (from rel in Rel
                           from value in JunkValue
                           select (rel, value)).ArrayOf(count)
            select new HalLinkJunk(pairs
                .DistinctBy(p => p.rel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p.rel, p => (object?)p.value)))
            .ToArbitrary();

    public static Arbitrary<TemplateValue> TemplateValues()
        => (from length in Gen.Choose(0, 16)
            from chars in Gen.Choose(1, 0xFFFF).Select(i => (char)i).Where(c => !char.IsSurrogate(c)).ArrayOf(length)
            select new TemplateValue(new string(chars))).ToArbitrary();

    private static Gen<object?> JunkValue => Gen.OneOf(
        Gen.Constant<object?>(null),
        Text(0, 12).Select(s => (object?)s),
        ArbMap.Default.GeneratorFor<int>().Select(i => (object?)i),
        Gen.Elements<object?>(true, false),
        Href.Select(h => (object?)new { href = h }),
        (from count in Gen.Choose(0, 3)
         from hrefs in Href.ArrayOf(count)
         select (object?)hrefs.Select(h => new { href = h }).ToArray()));
}
