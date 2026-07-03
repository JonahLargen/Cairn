using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnTestingTests
{
    [Fact]
    public void Parses_and_asserts_links_and_actions_from_json()
    {
        const string json = """
            {"id":1,"_links":{"self":{"href":"/o/1"}},"_actions":{"cancel":{"href":"/o/1/cancel","method":"POST"}}}
            """;

        HypermediaResponse.Parse(json).Should()
            .HaveLink("self", "/o/1")
            .And.HaveAffordance("cancel").WithMethod(HttpMethod.Post).WithHref("/o/1/cancel");
    }

    [Fact]
    public void Parses_multi_link_relations_and_curies_emitted_as_link_arrays()
    {
        // Cairn's own output: a multi-link rel and a configured curie both emit JSON arrays.
        const string json = """
            {
                "id": 1,
                "_links": {
                    "self": {"href": "/o/1"},
                    "acme:children": [{"href": "/o/1/c/1", "name": "first"}, {"href": "/o/1/c/2", "name": "second"}],
                    "curies": [{"href": "/rels/{rel}", "name": "acme", "templated": true}]
                }
            }
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        hypermedia.Should()
            .HaveLink("self", "/o/1")
            .And.HaveLink("acme:children", "/o/1/c/2")
            .And.HaveLink("curies");

        Assert.Equal(2, hypermedia.AllLinks["acme:children"].Count);
        Assert.Equal("first", hypermedia.AllLinks["acme:children"][0].Name);
        Assert.Equal("/o/1/c/1", hypermedia.Links["acme:children"].Href);
    }

    [Fact]
    public void Skips_malformed_relation_values_instead_of_throwing()
    {
        const string json = """
            {"_links":{"self":{"href":"/o/1"},"broken":"not-a-link-object","empty":[]},"_actions":{"cancel":"nope"}}
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        hypermedia.Should().HaveLink("self").And.NotHaveLink("broken").And.NotHaveAffordance("cancel");
    }

    [Fact]
    public async Task Asserts_hypermedia_on_a_live_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new AssertOrderLinks()));

        await using var app = builder.Build();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("AssertCancel");
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new AssertOrder(id)))
            .WithName("AssertOrderById")
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/42");
        var hypermedia = await response.ReadHypermediaAsync();

        hypermedia.Should()
            .HaveSelfLink()
            .And.HaveAffordance("cancel").WithMethod(HttpMethod.Post)
            .And.NotHaveAffordance("delete")
            .And.NotHaveLink("parent");
    }

    [Fact]
    public void A_link_with_a_missing_or_empty_href_fails_parsing_with_a_clear_message()
    {
        var missing = Assert.Throws<FormatException>(() => HypermediaResponse.Parse("""{"_links":{"self":{"title":"no href"}}}"""));
        Assert.Contains("'self'", missing.Message);
        Assert.Contains("href", missing.Message);

        var empty = Assert.Throws<FormatException>(() => HypermediaResponse.Parse("""{"_links":{"self":{"href":""}}}"""));
        Assert.Contains("'self'", empty.Message);

        // A member of a HAL link array is validated too.
        var array = Assert.Throws<FormatException>(() => HypermediaResponse.Parse("""{"_links":{"item":[{"href":"/i/1"},{"name":"second"}]}}"""));
        Assert.Contains("'item'", array.Message);

        var action = Assert.Throws<FormatException>(() => HypermediaResponse.Parse("""{"_actions":{"cancel":{"method":"POST"}}}"""));
        Assert.Contains("'cancel'", action.Message);
        Assert.Contains("href", action.Message);
    }

    [Fact]
    public void Failed_assertions_throw_CairnAssertionException_describing_the_actual_hypermedia()
    {
        var hypermedia = HypermediaResponse.Parse("""{"_links":{"self":{"href":"/o/1"}},"_actions":{"cancel":{"href":"/o/1/cancel","method":"POST"}}}""");

        var missingLink = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveLink("parent"));
        Assert.Contains("'parent'", missingLink.Message);
        Assert.Contains("'self'", missingLink.Message);

        var wrongHref = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveLink("self", "/o/2"));
        Assert.Contains("'/o/2'", wrongHref.Message);
        Assert.Contains("'/o/1'", wrongHref.Message);

        var unexpectedLink = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().NotHaveLink("self"));
        Assert.Contains("'self'", unexpectedLink.Message);

        var wrongMethod = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveAffordance("cancel").WithMethod(HttpMethod.Delete));
        Assert.Contains("'DELETE'", wrongMethod.Message);
        Assert.Contains("'POST'", wrongMethod.Message);
    }

    [Fact]
    public void Asserts_templated_links()
    {
        var hypermedia = HypermediaResponse.Parse("""{"_links":{"self":{"href":"/o/1"},"search":{"href":"/orders{?status,page}","templated":true}}}""");

        hypermedia.Should().HaveTemplatedLink("search");
        Assert.True(hypermedia.Links["search"].Templated);
        Assert.False(hypermedia.Links["self"].Templated);

        var notTemplated = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveTemplatedLink("self"));
        Assert.Contains("templated", notTemplated.Message);
    }

    [Fact]
    public void Parses_and_asserts_embedded_resources()
    {
        // Cairn's wire shape: a single embed is an object, a collection embed is an array,
        // each resource decorated with its own _links (see CairnEmbeddedTests).
        const string json = """
            {
                "id": 5,
                "_links": {"self": {"href": "/orders/5"}},
                "_embedded": {
                    "customer": {"id": 99, "_links": {"self": {"href": "/customers/99"}}},
                    "item": [
                        {"id": 1, "_links": {"self": {"href": "/items/1"}}},
                        {"id": 2, "_links": {"self": {"href": "/items/2"}}}
                    ]
                }
            }
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        hypermedia.Should()
            .HaveSelfLink()
            .HaveEmbedded("customer").HaveLink("self", "/customers/99");

        Assert.Equal(2, hypermedia.Embedded["item"].Count);
        Assert.Equal("/items/2", hypermedia.Embedded["item"][1].Links["self"].Href);

        var missing = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveEmbedded("supplier"));
        Assert.Contains("'supplier'", missing.Message);
        Assert.Contains("'customer'", missing.Message);
    }

    [Fact]
    public void Parses_and_asserts_hal_forms_templates_with_field_details()
    {
        // Cairn's HAL-FORMS wire shape (see CairnFormatTests / CairnHalFormsFieldTests).
        const string json = """
            {
                "_links": {"self": {"href": "/o/42"}},
                "_templates": {
                    "cancel": {
                        "method": "POST",
                        "target": "/o/42/cancel",
                        "contentType": "application/json",
                        "properties": [
                            {"name": "reason", "required": true, "type": "text", "maxLength": 200, "regex": "^[a-z]+$"},
                            {"name": "severity", "type": "number", "min": 1, "max": 5},
                            {"name": "status", "prompt": "Order status", "options": {"inline": [{"prompt": "Pending", "value": "Pending"}, {"prompt": "Shipped", "value": "Shipped"}]}},
                            {"name": "id", "readOnly": true}
                        ]
                    }
                }
            }
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        hypermedia.Should()
            .HaveTemplate("cancel")
            .WithMethod(HttpMethod.Post)
            .WithTarget("/o/42/cancel")
            .WithContentType("application/json")
            .HaveField("reason").ThatIsRequired().WithType("text").WithRegex("^[a-z]+$")
            .And.HaveField("severity").ThatIsOptional().WithType("number")
            .And.HaveField("status").WithPrompt("Order status")
            .And.HaveField("id").ThatIsReadOnly();

        var template = hypermedia.Templates["cancel"];
        Assert.Equal(200, template.Fields.Single(field => field.Name == "reason").MaxLength);
        Assert.Equal(1d, template.Fields.Single(field => field.Name == "severity").Min);
        Assert.Equal(5d, template.Fields.Single(field => field.Name == "severity").Max);
        Assert.Equal(["Pending", "Shipped"], template.Fields.Single(field => field.Name == "status").Options);

        // Templates keep powering the affordance view.
        hypermedia.Should().HaveAffordance("cancel").WithMethod(HttpMethod.Post).WithHref("/o/42/cancel");

        var missingField = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveTemplate("cancel").HaveField("nope"));
        Assert.Contains("'nope'", missingField.Message);
        Assert.Contains("'reason'", missingField.Message);

        var notRequired = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveTemplate("cancel").HaveField("severity").ThatIsRequired());
        Assert.Contains("'severity'", notRequired.Message);
    }

    [Fact]
    public void HaveLinkMatching_matches_placeholders_and_prefixes()
    {
        // Host and port vary per test run; the pattern makes assertions robust without Assert.EndsWith.
        var hypermedia = HypermediaResponse.Parse(
            """{"_links":{"self":{"href":"http://localhost:5123/orders/42"},"item":[{"href":"/o/1"},{"href":"/o/2"}]}}""");

        hypermedia.Should()
            .HaveLinkMatching("self", "http://{host}/orders/{id}")
            .And.HaveLinkMatching("self", "http://localhost:5123/orders/*")
            .And.HaveLinkMatching("self", "*")
            .And.HaveLinkMatching("item", "/o/{id}");
    }

    [Fact]
    public void HaveLinkMatching_placeholder_matches_exactly_one_segment()
    {
        var hypermedia = HypermediaResponse.Parse("""{"_links":{"self":{"href":"/orders/42/items"}}}""");

        // {id} must not swallow "/": "/orders/{id}" cannot match a two-segment tail.
        var mismatch = Assert.Throws<CairnAssertionException>(
            () => hypermedia.Should().HaveLinkMatching("self", "/orders/{id}"));
        Assert.Contains("/orders/42/items", mismatch.Message);

        hypermedia.Should().HaveLinkMatching("self", "/orders/{id}/items");
    }

    [Fact]
    public void HaveLinkMatching_escapes_regex_metacharacters_in_literals()
    {
        var hypermedia = HypermediaResponse.Parse("""{"_links":{"search":{"href":"/orders?q=a+b"}}}""");

        hypermedia.Should().HaveLinkMatching("search", "/orders?q=a+b");

        // '.' is a literal, not "any character".
        Assert.Throws<CairnAssertionException>(() => hypermedia.Should().HaveLinkMatching("search", "/orders?q=a.b"));
    }

    [Fact]
    public void Affordance_and_template_targets_match_patterns_too()
    {
        var hypermedia = HypermediaResponse.Parse(
            """
            {
                "_links": {"self": {"href": "http://localhost:5123/o/42"}},
                "_actions": {"cancel": {"href": "http://localhost:5123/o/42/cancel", "method": "POST"}},
                "_templates": {"update": {"method": "PUT", "target": "http://localhost:5123/o/42"}}
            }
            """);

        hypermedia.Should()
            .HaveAffordance("cancel").WithHrefMatching("http://{host}/o/{id}/cancel")
            .And.HaveTemplate("update").WithTargetMatching("http://{host}/o/{id}");

        var wrong = Assert.Throws<CairnAssertionException>(
            () => hypermedia.Should().HaveAffordance("cancel").WithHrefMatching("/o/{id}/cancel"));
        Assert.Contains("cancel", wrong.Message);
    }

    [Fact]
    public void A_template_without_a_target_falls_back_to_the_self_link()
    {
        const string json = """{"_links":{"self":{"href":"/o/42"}},"_templates":{"default":{"method":"PUT"}}}""";

        HypermediaResponse.Parse(json).Should().HaveTemplate("default").WithMethod(HttpMethod.Put).WithTarget("/o/42");
    }

    [Fact]
    public void Parse_throws_on_an_array_root_and_ParseAll_parses_each_element()
    {
        const string json = """[{"id":1,"_links":{"self":{"href":"/o/1"}}},{"id":2,"_links":{"self":{"href":"/o/2"}}}]""";

        // A silently-empty response would let every negative assertion pass; the error points at ParseAll.
        var arrayRoot = Assert.Throws<FormatException>(() => HypermediaResponse.Parse(json));
        Assert.Contains("ParseAll", arrayRoot.Message);

        var resources = HypermediaResponse.ParseAll(json);
        Assert.Equal(2, resources.Count);
        Assert.Equal("/o/1", resources[0].Links["self"].Href);
        Assert.Equal("/o/2", resources[1].Links["self"].Href);

        var objectRoot = Assert.Throws<FormatException>(() => HypermediaResponse.ParseAll("""{"id":1}"""));
        Assert.Contains("Parse", objectRoot.Message);
    }

    [Fact]
    public async Task ReadHypermediaListAsync_parses_an_array_root_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"_links":{"self":{"href":"/i/1"}}}]"""),
        };

        var resources = await response.ReadHypermediaListAsync();

        var resource = Assert.Single(resources);
        Assert.Equal("/i/1", resource.Links["self"].Href);
    }

    [Fact]
    public void An_absent_method_defaults_to_get_for_actions_and_templates()
    {
        var hypermedia = HypermediaResponse.Parse(
            """{"_links":{"self":{"href":"/o/1"}},"_actions":{"reload":{"href":"/o/1"}},"_templates":{"default":{}}}""");

        // HAL-FORMS prescribes GET when a template omits its method; _actions mirrors that.
        Assert.Equal("GET", hypermedia.Affordances["reload"].Method);
        Assert.Equal("GET", hypermedia.Templates["default"].Method);
        hypermedia.Should().HaveAffordance("reload").WithMethod(HttpMethod.Get);
    }

    [Fact]
    public void Parses_bare_string_inline_options()
    {
        var hypermedia = HypermediaResponse.Parse(
            """{"_templates":{"update":{"target":"/o/1","properties":[{"name":"status","options":{"inline":["open","closed",{"value":"archived"}]}}]}}}""");

        var field = Assert.Single(hypermedia.Templates["update"].Fields);
        Assert.Equal(["open", "closed", "archived"], field.Options);
    }

    [Fact]
    public void Parses_link_type_deprecation_hreflang_and_profile()
    {
        var hypermedia = HypermediaResponse.Parse(
            """{"_links":{"self":{"href":"/o/1","type":"application/hal+json","deprecation":"https://api.example/deprecations/o","hreflang":"en","profile":"https://api.example/profiles/order"}}}""");

        var link = hypermedia.Links["self"];
        Assert.Equal("application/hal+json", link.Type);
        Assert.Equal("https://api.example/deprecations/o", link.Deprecation);
        Assert.Equal("en", link.Hreflang);
        Assert.Equal("https://api.example/profiles/order", link.Profile);
    }

    [Fact]
    public async Task GetHypermediaAsync_asserts_hal_forms_on_a_live_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new TestingFormOrderLinks());
        });

        await using var app = builder.Build();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("TestingFormCancel");
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new TestingFormOrder(id)))
            .WithName("TestingFormOrderById")
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var hypermedia = await client.GetHypermediaAsync("/orders/7");

        // The response's sole template is keyed under the reserved "default" HAL-FORMS name.
        hypermedia.Should()
            .HaveSelfLink()
            .And.HaveTemplate("default")
            .WithMethod(HttpMethod.Post)
            .WithContentType("application/json")
            .HaveField("reason").ThatIsRequired().WithType("text").WithRegex("^[a-z ]+$")
            .And.HaveField("notify").ThatIsOptional();

        // A bool input has no HAL-FORMS type of its own; it is described via a two-value options list.
        Assert.Equal(["true", "false"], hypermedia.Templates["default"].Fields.Single(field => field.Name == "notify").Options);
    }

    [Fact]
    public async Task GetHypermediaAsync_fails_with_a_clear_message_on_a_non_success_status()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new AssertOrderLinks()));

        await using var app = builder.Build();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var failure = await Assert.ThrowsAsync<CairnAssertionException>(() => client.GetHypermediaAsync("/missing"));
        Assert.Contains("404", failure.Message);
        Assert.Contains("/missing", failure.Message);
    }

    [Fact]
    public async Task Asserts_embedded_resources_on_a_live_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new TestingEmbOrderLinks());
            o.AddLinks(new TestingEmbItemLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new TestingEmbOrder(id) { Items = [new TestingEmbItem(1), new TestingEmbItem(2)] }))
            .WithName("TestingEmbGetOrder").WithLinks();
        app.MapGet("/items/{id:int}", (int id) => TypedResults.Ok(new TestingEmbItem(id))).WithName("TestingEmbGetItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var hypermedia = await client.GetHypermediaAsync("/orders/5");

        hypermedia.Should().HaveEmbedded("item").HaveSelfLink();
        Assert.Equal(2, hypermedia.Embedded["item"].Count);
        Assert.EndsWith("/items/2", hypermedia.Embedded["item"][1].Links["self"].Href);
    }

    [Fact]
    public void Snapshot_renders_stable_sorted_indented_json()
    {
        const string json = """{"b":2,"a":1,"_links":{"self":{"href":"/o/1"}}}""";

        var snapshot = HypermediaSnapshot.Render(json);

        Assert.Equal("""
            {
              "_links": {
                "self": {
                  "href": "/o/1"
                }
              },
              "a": 1,
              "b": 2
            }
            """.Replace("\r\n", "\n"), snapshot);

        // Key order in the source payload does not change the snapshot.
        Assert.Equal(snapshot, HypermediaSnapshot.Render("""{"_links":{"self":{"href":"/o/1"}},"a":1,"b":2}"""));
    }

    [Fact]
    public void Snapshot_can_reduce_to_hypermedia_parts_and_normalize_hrefs()
    {
        const string json = """
            {
                "id": 5,
                "name": "volatile data",
                "_links": {"self": {"href": "http://localhost:5123/orders/5"}},
                "_templates": {"cancel": {"method": "POST", "target": "http://localhost:5123/orders/5/cancel"}},
                "_embedded": {"item": [{"id": 1, "_links": {"self": {"href": "http://localhost:5123/items/1"}}}]}
            }
            """;

        var snapshot = HypermediaSnapshot.Render(json, new HypermediaSnapshotOptions
        {
            HypermediaOnly = true,
            NormalizeHref = href => href.Replace("http://localhost:5123", "<host>"),
        });

        Assert.Equal("""
            {
              "_embedded": {
                "item": [
                  {
                    "_links": {
                      "self": {
                        "href": "<host>/items/1"
                      }
                    }
                  }
                ]
              },
              "_links": {
                "self": {
                  "href": "<host>/orders/5"
                }
              },
              "_templates": {
                "cancel": {
                  "method": "POST",
                  "target": "<host>/orders/5/cancel"
                }
              }
            }
            """.Replace("\r\n", "\n"), snapshot);
    }

    private sealed record AssertOrder(int Id);

    private sealed class AssertOrderLinks : LinkConfig<AssertOrder>
    {
        public override void Configure(ILinkBuilder<AssertOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("AssertOrderById", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("AssertCancel", new { id = order.Id })).Method("POST");
        }
    }

    private sealed record TestingFormOrder(int Id);

    private sealed class TestingCancelInput
    {
        [Required]
        [RegularExpression("^[a-z ]+$")]
        public string Reason { get; init; } = "";

        public bool Notify { get; init; }
    }

    private sealed class TestingFormOrderLinks : LinkConfig<TestingFormOrder>
    {
        public override void Configure(ILinkBuilder<TestingFormOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("TestingFormOrderById", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("TestingFormCancel", new { id = order.Id }))
                .Method("POST")
                .Accepts<TestingCancelInput>();
        }
    }

    private sealed record TestingEmbOrder(int Id)
    {
        [JsonIgnore]
        public TestingEmbItem[] Items { get; init; } = [];
    }

    private sealed record TestingEmbItem(int Id);

    private sealed class TestingEmbOrderLinks : LinkConfig<TestingEmbOrder>
    {
        public override void Configure(ILinkBuilder<TestingEmbOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("TestingEmbGetOrder", new { id = order.Id }));
            builder.EmbedMany("item", order => order.Items);
        }
    }

    private sealed class TestingEmbItemLinks : LinkConfig<TestingEmbItem>
    {
        public override void Configure(ILinkBuilder<TestingEmbItem> builder)
            => builder.Self(item => LinkTarget.Route("TestingEmbGetItem", new { id = item.Id }));
    }
}
