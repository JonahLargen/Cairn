using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// A host-configured DictionaryKeyPolicy renames dictionary keys ([JsonPropertyName] does not apply to them),
// but hypermedia keys are protocol identifiers and must be emitted verbatim.
public class CairnDictionaryKeyPolicyTests
{
    [Fact]
    public async Task Link_embedded_and_action_keys_survive_a_host_dictionary_key_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddLinks(new PolicyOrderLinks());
            o.AddLinks(new PolicyChildLinks());
        });
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper);

        await using var app = builder.Build();
        app.MapGet("/pol/{id:int}", (int id) => TypedResults.Ok(new PolicyOrder(id))).WithName("PolGetOrder").WithLinks();
        app.MapPost("/pol/{id:int}/re-order", (int id) => TypedResults.Ok()).WithName("PolReOrder");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/pol/7")).RootElement;

        var links = root.GetProperty("_links");
        Assert.True(links.TryGetProperty("self", out _));
        Assert.True(links.TryGetProperty("acme:widget", out _));
        Assert.True(links.TryGetProperty("curies", out _));
        Assert.False(links.TryGetProperty("SELF", out _));
        Assert.False(links.TryGetProperty("ACME:WIDGET", out _));

        Assert.True(root.GetProperty("_actions").TryGetProperty("re-order", out _));
        Assert.True(root.GetProperty("_embedded").TryGetProperty("acme:child", out _));
    }

    [Fact]
    public async Task Hal_forms_template_keys_survive_a_host_dictionary_key_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new PolicyOrderLinks());
            o.AddLinks(new PolicyChildLinks());
        });
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper);

        await using var app = builder.Build();
        app.MapGet("/polf/{id:int}", (int id) => TypedResults.Ok(new PolicyOrder(id))).WithName("PolFormsGetOrder").WithLinks();
        app.MapPost("/polf/{id:int}/re-order", (int id) => TypedResults.Ok()).WithName("PolFormsReOrder");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/polf/7")).RootElement;

        Assert.True(root.GetProperty("_templates").TryGetProperty("re-order", out var template));
        Assert.False(root.GetProperty("_templates").TryGetProperty("RE_ORDER", out _));
        Assert.Equal("POST", template.GetProperty("method").GetString());
    }

    [Fact]
    public async Task Problem_members_and_rels_survive_a_host_dictionary_key_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper);

        await using var app = builder.Build();
        app.MapGet("/polp", () => CairnResults.Problem(409, title: "Conflict", detail: "Stale.")
            .WithExtension("traceId", "abc")
            .WithLink("describedby", "https://errors.example.com/conflict")
            .WithAction("retry-payment", "/polp/retry"));

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/polp");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("Conflict", root.GetProperty("title").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.Equal("abc", root.GetProperty("traceId").GetString());
        Assert.True(root.GetProperty("_links").GetProperty("describedby").TryGetProperty("href", out _));
        Assert.True(root.GetProperty("_actions").GetProperty("retry-payment").TryGetProperty("method", out _));
        Assert.False(root.TryGetProperty("TITLE", out _));
    }

    private sealed record PolicyOrder(int Id)
    {
        public PolicyChild Child { get; } = new(Id);
    }

    private sealed record PolicyChild(int Id);

    private sealed class PolicyOrderLinks : LinkConfig<PolicyOrder>
    {
        public override void Configure(ILinkBuilder<PolicyOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/pol/{o.Id}"));
            builder.Link("acme:widget", o => LinkTarget.Uri($"/widgets/{o.Id}"));
            builder.Affordance("re-order", o => LinkTarget.Uri($"/pol/{o.Id}/re-order")).Post();

            // A second affordance keeps "re-order" under its own template key (a sole template would be
            // keyed "default", which is not what this test is about).
            builder.Affordance("archive", o => LinkTarget.Uri($"/pol/{o.Id}/archive")).Post();
            builder.Embed("acme:child", o => o.Child);
        }
    }

    private sealed class PolicyChildLinks : LinkConfig<PolicyChild>
    {
        public override void Configure(ILinkBuilder<PolicyChild> builder)
            => builder.Self(c => LinkTarget.Uri($"/pol/{c.Id}/child"));
    }
}
