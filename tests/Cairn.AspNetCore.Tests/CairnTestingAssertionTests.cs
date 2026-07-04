using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// The assertion surface added on top of Cairn.Testing's hypermedia parser: NotHaveTemplate, embedded-count and
// NotHaveEmbedded, the .And chain stepping back out of an embedded resource, WithContentType on an affordance,
// and the transport-level HttpResponseMessage helpers (status / content type / ETag).
public class CairnTestingAssertionTests
{
    [Fact]
    public void NotHaveTemplate_passes_when_absent_and_fails_when_present()
    {
        const string json = """
            {"_links":{"self":{"href":"/o/1"}},"_templates":{"update":{"target":"/o/1","method":"PUT"}}}
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        // Passes for a template the response does not expose, and chains back to the response.
        hypermedia.Should()
            .NotHaveTemplate("delete")
            .And.HaveTemplate("update").WithMethod(HttpMethod.Put);

        var present = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().NotHaveTemplate("update"));
        Assert.Contains("'update'", present.Message);
        Assert.Contains("but it does", present.Message);
    }

    [Fact]
    public void HaveEmbedded_with_a_count_asserts_the_number_of_embedded_resources()
    {
        const string json = """
            {
                "_links": {"self": {"href": "/orders/5"}},
                "_embedded": {
                    "item": [
                        {"_links": {"self": {"href": "/items/1"}}},
                        {"_links": {"self": {"href": "/items/2"}}}
                    ],
                    "customer": {"_links": {"self": {"href": "/customers/99"}}}
                }
            }
            """;

        // The count passes for a collection embed and a single embed (which counts as one), and the returned
        // chain drills into the first embedded resource before .And steps back out to the order.
        HypermediaResponse.Parse(json).Should()
            .HaveEmbedded("item", 2).HaveLink("self", "/items/1")
            .And.HaveEmbedded("customer", 1).HaveLink("self", "/customers/99")
            .And.HaveSelfLink();

        var tooMany = Assert.Throws<CairnAssertionException>(
            () => HypermediaResponse.Parse(json).Should().HaveEmbedded("item", 3));
        Assert.Contains("embed 3 'item' resources", tooMany.Message);
        Assert.Contains("it embeds 2", tooMany.Message);

        // A count of one uses the singular noun.
        var singular = Assert.Throws<CairnAssertionException>(
            () => HypermediaResponse.Parse(json).Should().HaveEmbedded("item", 1));
        Assert.Contains("embed 1 'item' resource,", singular.Message);

        // A relation that is not embedded at all embeds zero.
        var missing = Assert.Throws<CairnAssertionException>(
            () => HypermediaResponse.Parse(json).Should().HaveEmbedded("supplier", 2));
        Assert.Contains("it embeds 0", missing.Message);
    }

    [Fact]
    public void HaveEmbedded_with_a_zero_count_matches_an_absent_relation_and_stays_on_the_response()
    {
        const string json = """
            {"_links":{"self":{"href":"/orders/5"}},"_embedded":{"item":{"_links":{"self":{"href":"/items/1"}}}}}
            """;

        // Expecting zero of a relation that is not embedded passes; with nothing to drill into, the chain stays
        // on the response rather than throwing on a missing first element.
        HypermediaResponse.Parse(json).Should()
            .HaveEmbedded("supplier", 0)
            .HaveSelfLink();

        var present = Assert.Throws<CairnAssertionException>(
            () => HypermediaResponse.Parse(json).Should().HaveEmbedded("item", 0));
        Assert.Contains("embed 0 'item' resources", present.Message);
        Assert.Contains("it embeds 1", present.Message);
    }

    [Fact]
    public void HaveEmbedded_rejects_a_negative_count()
    {
        var hypermedia = HypermediaResponse.Parse("""{"_links":{"self":{"href":"/o/1"}}}""");

        Assert.Throws<ArgumentOutOfRangeException>(() => hypermedia.Should().HaveEmbedded("item", -1));
    }

    [Fact]
    public void NotHaveEmbedded_passes_when_absent_and_fails_when_present()
    {
        const string json = """
            {"_links":{"self":{"href":"/o/1"}},"_embedded":{"item":[{"_links":{"self":{"href":"/i/1"}}},{"_links":{"self":{"href":"/i/2"}}}]}}
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        hypermedia.Should().NotHaveEmbedded("supplier").And.HaveSelfLink();

        var present = Assert.Throws<CairnAssertionException>(() => hypermedia.Should().NotHaveEmbedded("item"));
        Assert.Contains("'item'", present.Message);
        Assert.Contains("it embeds 2", present.Message);
    }

    [Fact]
    public void Embedded_assertions_chain_back_to_the_embedding_response()
    {
        const string json = """
            {
                "id": 5,
                "_links": {"self": {"href": "/orders/5"}},
                "_embedded": {
                    "customer": {"_links": {"self": {"href": "/customers/99"}}},
                    "item": [
                        {"_links": {"self": {"href": "/items/1"}}},
                        {"_links": {"self": {"href": "/items/2"}}}
                    ]
                }
            }
            """;

        // .And after an embedded assertion returns to the order — so a single chain crosses the _embedded
        // boundary and comes back to keep asserting on the parent.
        HypermediaResponse.Parse(json).Should()
            .HaveSelfLink()
            .HaveEmbedded("customer").HaveLink("self", "/customers/99")
            .And.HaveEmbedded("item").HaveLink("self", "/items/1")
            .And.HaveLink("self", "/orders/5");
    }

    [Fact]
    public void Nested_embedded_and_affordance_assertions_pop_one_level_per_and()
    {
        const string json = """
            {
                "_links": {"self": {"href": "/orders/5"}},
                "_embedded": {
                    "customer": {
                        "_links": {"self": {"href": "/customers/99"}},
                        "_actions": {"email": {"href": "/customers/99/email", "method": "POST"}}
                    }
                }
            }
            """;

        // An affordance drilled from an embedded resource pops back to that resource, which in turn pops back to
        // the order — one level per .And.
        HypermediaResponse.Parse(json).Should()
            .HaveEmbedded("customer")
                .HaveAffordance("email").WithMethod(HttpMethod.Post)
                .And.HaveLink("self", "/customers/99")
            .And.HaveLink("self", "/orders/5");
    }

    [Fact]
    public void Affordance_content_type_is_carried_from_the_backing_template()
    {
        const string json = """
            {
                "_links": {"self": {"href": "/o/42"}},
                "_templates": {
                    "cancel": {"method": "POST", "target": "/o/42/cancel", "contentType": "application/json"}
                }
            }
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        Assert.Equal("application/json", hypermedia.Affordances["cancel"].ContentType);

        hypermedia.Should()
            .HaveAffordance("cancel").WithContentType("application/json").WithMethod(HttpMethod.Post);

        var wrong = Assert.Throws<CairnAssertionException>(
            () => hypermedia.Should().HaveAffordance("cancel").WithContentType("application/x-www-form-urlencoded"));
        Assert.Contains("application/x-www-form-urlencoded", wrong.Message);
        Assert.Contains("application/json", wrong.Message);
    }

    [Fact]
    public void Affordance_content_type_is_read_from_actions_and_reports_none_when_absent()
    {
        const string json = """
            {
                "_links": {"self": {"href": "/o/1"}},
                "_actions": {
                    "submit": {"href": "/o/1/submit", "method": "POST", "contentType": "application/x-www-form-urlencoded"},
                    "reload": {"href": "/o/1", "method": "GET"}
                }
            }
            """;

        var hypermedia = HypermediaResponse.Parse(json);

        hypermedia.Should().HaveAffordance("submit").WithContentType("application/x-www-form-urlencoded");

        // An action without a contentType never matches, and its message reports "none" rather than an empty value.
        Assert.Null(hypermedia.Affordances["reload"].ContentType);
        var none = Assert.Throws<CairnAssertionException>(
            () => hypermedia.Should().HaveAffordance("reload").WithContentType("application/json"));
        Assert.Contains("'none'", none.Message);
    }

    [Fact]
    public void HttpResponse_should_asserts_status_content_type_and_etag()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/hal+json"),
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");

        response.Should()
            .HaveStatusCode(HttpStatusCode.OK)
            .And.HaveContentType("application/hal+json")   // the charset parameter is ignored
            .And.HaveContentType("APPLICATION/HAL+JSON")   // media types compare case-insensitively
            .And.HaveETag()
            .And.HaveETag("\"v1\"");
    }

    [Fact]
    public void HttpResponse_assertions_fail_with_clear_messages()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };

        var status = Assert.Throws<CairnAssertionException>(() => response.Should().HaveStatusCode(HttpStatusCode.OK));
        Assert.Contains("200 (OK)", status.Message);
        Assert.Contains("404 (NotFound)", status.Message);

        var contentType = Assert.Throws<CairnAssertionException>(() => response.Should().HaveContentType("application/json"));
        Assert.Contains("application/json", contentType.Message);
        Assert.Contains("text/plain", contentType.Message);

        var missingEtag = Assert.Throws<CairnAssertionException>(() => response.Should().HaveETag());
        Assert.Contains("ETag", missingEtag.Message);

        // No ETag present: the specific-value overload reports "none".
        var wrongEtag = Assert.Throws<CairnAssertionException>(() => response.Should().HaveETag("\"v1\""));
        Assert.Contains("\"v1\"", wrongEtag.Message);
        Assert.Contains("none", wrongEtag.Message);
    }

    [Fact]
    public void HaveContentType_reports_none_when_the_response_has_no_content_type()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NoContent)
        {
            Content = new ByteArrayContent([]),   // ByteArrayContent sets no Content-Type header
        };

        var noType = Assert.Throws<CairnAssertionException>(() => response.Should().HaveContentType("application/json"));
        Assert.Contains("'none'", noType.Message);
    }

    [Fact]
    public void HttpResponse_should_asserts_a_weak_etag_verbatim()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"v5\"", isWeak: true);

        // The expectation reads exactly as the tag on the wire, W/ prefix included.
        response.Should().HaveETag().And.HaveETag("W/\"v5\"");

        var strong = Assert.Throws<CairnAssertionException>(() => response.Should().HaveETag("\"v5\""));
        Assert.Contains("W/\"v5\"", strong.Message);
    }

    [Fact]
    public void HttpResponse_should_rejects_a_null_response()
    {
        HttpResponseMessage response = null!;

        Assert.Throws<ArgumentNullException>(() => response.Should());
    }

    [Fact]
    public async Task Http_helpers_assert_status_content_type_and_etag_on_a_live_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new TestingHttpOrderLinks()));

        await using var app = builder.Build();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("TestingHttpCancel");
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new TestingHttpOrder(id)))
            .WithName("TestingHttpOrderById")
            .WithLinks()
            .WithETag((TestingHttpOrder order) => $"v{order.Id}");

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/orders/7");

        response.Should()
            .HaveStatusCode(HttpStatusCode.OK)
            .And.HaveContentType("application/json")
            .And.HaveETag()
            .And.HaveETag("\"v7\"");

        var hypermedia = await response.ReadHypermediaAsync();
        hypermedia.Should()
            .HaveSelfLink()
            .And.HaveAffordance("cancel").WithMethod(HttpMethod.Post);
    }

    private sealed record TestingHttpOrder(int Id);

    private sealed class TestingHttpOrderLinks : LinkConfig<TestingHttpOrder>
    {
        public override void Configure(ILinkBuilder<TestingHttpOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("TestingHttpOrderById", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("TestingHttpCancel", new { id = order.Id })).Method("POST");
        }
    }
}
