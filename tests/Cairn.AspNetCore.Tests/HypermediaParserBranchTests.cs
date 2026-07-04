using System.Net;
using System.Text;
using System.Text.Json;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Parser branches for the shapes a server can legally (or sloppily) put on the wire: scalar resource
// roots, actions and templates without a method, malformed link objects, non-object _embedded, and
// field metadata whose options/constraints carry the wrong JSON kind.
public class HypermediaParserBranchTests
{
    [Fact]
    public async Task A_scalar_resource_body_carries_no_hypermedia()
    {
        var result = await GetAsync<int>("42");

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Resource!.RequireValue());
        Assert.Empty(result.Resource.Links);
        Assert.Empty(result.Resource.Affordances);
    }

    [Fact]
    public async Task An_action_without_a_method_defaults_to_GET()
    {
        var result = await GetAsync<JsonElement>("""{ "_actions": { "poke": { "href": "/poke" } } }""");

        Assert.Equal("GET", result.Resource!.Affordances["poke"].Method);
    }

    [Fact]
    public async Task A_template_without_a_method_defaults_to_GET()
    {
        var result = await GetAsync<JsonElement>("""{ "_templates": { "look": { "target": "/look" } } }""");

        Assert.Equal("GET", result.Resource!.Affordances["look"].Method);
    }

    [Fact]
    public async Task A_non_object_embedded_section_is_ignored()
    {
        var result = await GetAsync<JsonElement>("""{ "_embedded": [ { "id": 1 } ] }""");

        // _embedded must be an object keyed by relation; an array cannot be resolved by relation.
        Assert.Empty(result.Resource!.Embedded<JsonElement>("items"));
    }

    [Fact]
    public async Task Malformed_link_entries_are_skipped_without_aborting_the_response()
    {
        const string body = """
            {
              "_links": {
                "scalar": "/not-an-object",
                "empty": {},
                "blank": { "href": "   " },
                "good": { "href": "/good" }
              }
            }
            """;
        var result = await GetAsync<JsonElement>(body);

        Assert.False(result.Resource!.HasLink("scalar"));
        Assert.False(result.Resource.HasLink("empty"));
        Assert.False(result.Resource.HasLink("blank"));
        Assert.Equal("/good", result.Resource.Links["good"].Href);
    }

    [Fact]
    public async Task An_explicit_templated_false_link_is_not_templated()
    {
        var result = await GetAsync<JsonElement>("""{ "_links": { "item": { "href": "/item", "templated": false } } }""");

        Assert.False(result.Resource!.Links["item"].Templated);
    }

    [Fact]
    public async Task Field_options_with_the_wrong_json_kind_parse_as_empty()
    {
        const string body = """
            {
              "_templates": {
                "edit": {
                  "target": "/edit",
                  "properties": [
                    { "name": "a", "options": "not an object" },
                    { "name": "b", "options": { "inline": "not an array", "selectedValues": "not an array" } },
                    { "name": "c", "options": { "link": { "href": "/opts" } } }
                  ]
                }
              }
            }
            """;
        var result = await GetAsync<JsonElement>(body);

        var fields = result.Resource!.Fields("edit");
        Assert.All(fields, field => Assert.Empty(field.Options));
        Assert.All(fields, field => Assert.Empty(field.SelectedValues));
        Assert.Equal("/opts", fields[2].OptionsLink);
    }

    [Fact]
    public async Task Field_constraints_with_the_wrong_json_kind_parse_as_absent()
    {
        const string body = """
            {
              "_templates": {
                "edit": {
                  "target": "/edit",
                  "properties": [{
                    "name": "a",
                    "required": "yes",
                    "minLength": "3",
                    "maxLength": 2.5,
                    "min": "low",
                    "max": 99999999999999999999
                  }]
                }
              }
            }
            """;
        var result = await GetAsync<JsonElement>(body);

        // A string "yes" is not a boolean, "3" is not a number, 2.5 is not an Int32, "low" is not a
        // double — each falls back to its default rather than failing the parse.
        var field = Assert.Single(result.Resource!.Fields("edit"));
        Assert.False(field.Required);
        Assert.Null(field.MinLength);
        Assert.Null(field.MaxLength);
        Assert.Null(field.Min);
        Assert.Equal(1e20, field.Max);   // too big for Int32, still a valid double
    }

    private static async Task<ClientResult<T>> GetAsync<T>(string body)
    {
        var http = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://localhost") };
        return await new CairnClient(http).GetAsync<T>("/thing");
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/prs.hal-forms+json"),
            });
    }
}
