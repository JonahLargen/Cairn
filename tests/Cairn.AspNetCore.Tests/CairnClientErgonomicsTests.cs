using System.Net;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

public class CairnClientErgonomicsTests
{
    [Fact]
    public async Task The_client_asks_for_the_hypermedia_it_can_parse()
    {
        await using var app = await BuildServerAsync();
        var client = new CairnClient(NewHttpClient(app));

        var result = await client.GetAsync<AcceptEcho>("/accept-echo");

        var accept = result.Resource!.RequireValue().Accept;
        Assert.Contains("application/prs.hal-forms+json", accept);
        Assert.Contains("application/hal+json", accept);
        Assert.Contains("application/json", accept);
    }

    [Fact]
    public async Task A_preconfigured_accept_header_is_left_alone()
    {
        await using var app = await BuildServerAsync();
        var http = NewHttpClient(app);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.custom+json");
        var client = new CairnClient(http);

        var result = await client.GetAsync<AcceptEcho>("/accept-echo");

        Assert.Equal("application/vnd.custom+json", result.Resource!.RequireValue().Accept);
    }

    [Fact]
    public async Task A_form_encoded_affordance_submits_its_body_as_a_form()
    {
        await using var app = await BuildServerAsync();
        var client = new CairnClient(NewHttpClient(app));

        var resource = (await client.GetAsync<Doc>("/form-doc")).Resource!;
        var affordance = resource.Affordances["submit"];
        Assert.Equal("application/x-www-form-urlencoded", affordance.ContentType);

        var result = await client.InvokeAsync<FormEcho>(affordance, new { name = "cairn", count = 3 });

        Assert.True(result.IsSuccess);
        Assert.Equal("cairn", result.Value!.Name);
        Assert.Equal(3, result.Value.Count);
        Assert.Equal("application/x-www-form-urlencoded", result.Value.ContentType);
    }

    [Fact]
    public async Task A_plus_json_affordance_keeps_the_declared_media_type()
    {
        await using var app = await BuildServerAsync();
        var client = new CairnClient(NewHttpClient(app));

        var resource = (await client.GetAsync<Doc>("/patch-doc")).Resource!;
        var result = await client.InvokeAsync<FormEcho>(resource.Affordances["patch"], new { name = "x" });

        Assert.True(result.IsSuccess);
        Assert.Equal("application/merge-patch+json", result.Value!.ContentType);
    }

    [Fact]
    public async Task Curies_resolve_a_prefixed_relations_documentation()
    {
        await using var app = await BuildServerAsync();
        var client = new CairnClient(NewHttpClient(app));

        var resource = (await client.GetAsync<Doc>("/curied-doc")).Resource!;

        var curie = Assert.Single(resource.Curies);
        Assert.Equal("acme", curie.Name);
        Assert.Equal("https://docs.example.com/rels/widget", resource.DocumentationFor("acme:widget"));
        Assert.Null(resource.DocumentationFor("self"));
        Assert.Null(resource.DocumentationFor("other:thing"));
    }

    [Fact]
    public async Task The_link_policy_is_enforced_on_every_redirect_hop()
    {
        await using var app = await BuildServerAsync();

        var services = new ServiceCollection();
        services.AddCairnClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.AllowLink = uri => uri.Host == "localhost";
        }).ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<CairnClient>();

        // A same-host redirect is followed to its target.
        var followed = await client.GetAsync<Doc>("/redirect-ok");
        Assert.True(followed.IsSuccess);
        Assert.Equal("landed", followed.Value!.Name);

        // A redirect escaping the allowed host is rejected even though the first URL passed the policy.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<Doc>("/redirect-evil"));
    }

    private static HttpClient NewHttpClient(WebApplication app)
    {
        var http = new HttpClient(app.GetTestServer().CreateHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        };
        return http;
    }

    private static async Task<WebApplication> BuildServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        var app = builder.Build();

        app.MapGet("/accept-echo", (HttpRequest request) =>
            Results.Json(new AcceptEcho(string.Join(", ", (IEnumerable<string?>)request.Headers.Accept))));

        app.MapGet("/form-doc", () => Results.Text(
            """
            {
              "name": "doc",
              "_templates": {
                "submit": { "method": "POST", "target": "/submit", "contentType": "application/x-www-form-urlencoded" }
              }
            }
            """, "application/prs.hal-forms+json"));

        app.MapPost("/submit", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            return Results.Json(new FormEcho(form["name"]!, int.Parse(form["count"]!), request.ContentType?.Split(';')[0] ?? ""));
        });

        app.MapGet("/patch-doc", () => Results.Text(
            """
            {
              "name": "doc",
              "_templates": {
                "patch": { "method": "PATCH", "target": "/patch", "contentType": "application/merge-patch+json" }
              }
            }
            """, "application/prs.hal-forms+json"));

        app.MapPatch("/patch", (HttpRequest request) =>
            Results.Json(new FormEcho("", 0, request.ContentType?.Split(';')[0] ?? "")));

        app.MapGet("/curied-doc", () => Results.Text(
            """
            {
              "name": "doc",
              "_links": {
                "self": { "href": "/curied-doc" },
                "curies": [{ "name": "acme", "href": "https://docs.example.com/rels/{rel}", "templated": true }],
                "acme:widget": { "href": "/widgets/1" }
              }
            }
            """, "application/hal+json"));

        app.MapGet("/redirect-ok", () => Results.Redirect("/landing"));
        app.MapGet("/landing", () => Results.Json(new Doc("landed")));
        app.MapGet("/redirect-evil", () => Results.Redirect("https://internal.evil.example/secrets"));

        await app.StartAsync();
        return app;
    }

    private sealed record AcceptEcho(string Accept);

    private sealed record Doc(string Name);

    private sealed record FormEcho(string Name, int Count, string ContentType);
}
