using System.Net;
using System.Text;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Relation types are case-insensitive (RFC 8288 §2.1). The server already compares them that way
// (LinkRelation); the client's lookups must too, or HasLink("Self") fails against a server emitting "self".
public class CairnClientRelCaseTests
{
    private const string Body = """
        {
          "id": 1,
          "_links": {
            "self": { "href": "/things/1" },
            "Curies": [{ "name": "ACME", "href": "https://docs.example.com/{rel}", "templated": true }],
            "acme:widget": { "href": "/widgets/1" }
          },
          "_actions": {
            "Archive": { "href": "/things/1/archive", "method": "POST" }
          },
          "_templates": {
            "Update": { "method": "PUT", "target": "/things/1", "properties": [{ "name": "note" }] }
          },
          "_embedded": {
            "Orders": [{ "id": 2, "_links": { "SELF": { "href": "/orders/2" } } }]
          }
        }
        """;

    [Fact]
    public async Task Link_lookups_ignore_relation_case()
    {
        var resource = await GetResourceAsync();

        Assert.True(resource.HasLink("Self"));
        Assert.Equal("/things/1", resource.Links["SELF"].Href);
        Assert.Single(resource.LinksFor("sElF"));
    }

    [Fact]
    public async Task Affordance_and_field_lookups_ignore_case()
    {
        var resource = await GetResourceAsync();

        Assert.True(resource.HasAffordance("archive"));
        Assert.True(resource.HasAffordance("update"));
        Assert.Equal("note", Assert.Single(resource.Fields("UPDATE")).Name);
    }

    [Fact]
    public async Task Embedded_lookups_ignore_relation_case()
    {
        var resource = await GetResourceAsync();

        var order = Assert.Single(resource.Embedded<Thing>("orders"));
        Assert.Equal(2, order.Value!.Id);
        Assert.Equal("/orders/2", order.Links["self"].Href);
    }

    [Fact]
    public async Task Curies_are_recognized_and_matched_case_insensitively()
    {
        var resource = await GetResourceAsync();

        Assert.Equal("ACME", Assert.Single(resource.Curies).Name);
        Assert.Equal("https://docs.example.com/widget", resource.DocumentationFor("acme:widget"));
    }

    private static async Task<Resource<Thing>> GetResourceAsync()
    {
        var http = new HttpClient(new StubHandler(Body)) { BaseAddress = new Uri("http://localhost") };
        return (await new CairnClient(http).GetAsync<Thing>("/things/1")).EnsureSuccess();
    }

    private sealed record Thing(int Id);

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/hal+json"),
            });
    }
}
