using System.Net;
using System.Text;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// HAL-FORMS fidelity: templates without a target submit to the resource itself, boolean values validate
// against the "true"/"false" options the server emits, and a parsed template keeps everything the wire
// carried (value, prompts, options.link, selectedValues, minLength, step, cols/rows, templated).
public class CairnClientHalFormsFidelityTests
{
    [Fact]
    public async Task A_template_without_a_target_submits_to_the_self_href()
    {
        const string body = """
            {
              "_links": { "self": { "href": "/orders/5" } },
              "_templates": { "cancel": { "method": "POST" } }
            }
            """;
        var resource = await GetResourceAsync(body);

        Assert.True(resource.HasAffordance("cancel"));
        Assert.Equal("/orders/5", resource.Affordances["cancel"].Href);
        Assert.Equal("POST", resource.Affordances["cancel"].Method);
    }

    [Fact]
    public async Task A_template_without_a_target_or_self_link_is_skipped()
    {
        const string body = """{ "_templates": { "cancel": { "method": "POST" } } }""";
        var resource = await GetResourceAsync(body);

        Assert.False(resource.HasAffordance("cancel"));
    }

    [Fact]
    public async Task A_bool_value_passes_validation_against_the_options_the_server_emits()
    {
        const string body = """
            {
              "_links": { "self": { "href": "/orders/5" } },
              "_templates": {
                "cancel": {
                  "method": "POST",
                  "target": "/orders/5/cancel",
                  "properties": [{
                    "name": "notify",
                    "options": { "inline": [{ "prompt": "True", "value": "true" }, { "prompt": "False", "value": "false" }] }
                  }]
                }
              }
            }
            """;
        var resource = await GetResourceAsync(body);

        // The server describes the bool as "true"/"false"; a JSON true must satisfy that, not ".NET True".
        var submitted = await resource.SubmitAsync("cancel", new { notify = true });
        Assert.True(submitted.IsSuccess);

        var declined = await resource.SubmitAsync("cancel", new { notify = false });
        Assert.True(declined.IsSuccess);

        var invalid = await Assert.ThrowsAsync<ArgumentException>(() => resource.SubmitAsync("cancel", new { notify = "maybe" }));
        Assert.Contains("must be one of: true, false", invalid.Message);
    }

    [Fact]
    public async Task A_parsed_template_keeps_everything_the_wire_carried()
    {
        const string body = """
            {
              "_templates": {
                "edit": {
                  "method": "PUT",
                  "target": "/profiles/7",
                  "properties": [{
                    "name": "bio",
                    "type": "textarea",
                    "value": "hello",
                    "templated": true,
                    "minLength": 2,
                    "maxLength": 400,
                    "step": 0.5,
                    "cols": 40,
                    "rows": 5,
                    "options": {
                      "inline": [{ "prompt": "Short bio", "value": "short" }, "long"],
                      "link": { "href": "/bios" },
                      "selectedValues": ["short"]
                    }
                  }]
                }
              }
            }
            """;
        var resource = await GetResourceAsync(body);

        var bio = Assert.Single(resource.Fields("edit"));
        Assert.Equal("hello", bio.Value);
        Assert.True(bio.Templated);
        Assert.Equal(2, bio.MinLength);
        Assert.Equal(400, bio.MaxLength);
        Assert.Equal(0.5, bio.Step);
        Assert.Equal(40, bio.Cols);
        Assert.Equal(5, bio.Rows);
        Assert.Equal([new AffordanceFieldOption("short") { Prompt = "Short bio" }, new AffordanceFieldOption("long")], bio.Options);
        Assert.Equal("/bios", bio.OptionsLink);
        Assert.Equal(["short"], bio.SelectedValues);
    }

    private static async Task<Resource<Order>> GetResourceAsync(string body)
    {
        var http = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://localhost") };
        return (await new CairnClient(http).GetAsync<Order>("/orders/5")).EnsureSuccess();
    }

    private sealed record Order(int Id);

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/prs.hal-forms+json") }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
