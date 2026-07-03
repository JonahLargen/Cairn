using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Curies must be surfaced for a curie-prefixed rel used in any rel-keyed section — not only _links.
public class CairnCurieSectionsTests
{
    [Fact]
    public async Task A_curied_affordance_name_surfaces_the_curie_in_links()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddLinks(new AffordanceCurieLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/ca/{id:int}", (int id) => TypedResults.Ok(new CurieSectionOrder(id))).WithName("CaGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/ca/7")).RootElement;

        Assert.True(root.GetProperty("_actions").TryGetProperty("acme:reorder", out _));

        var curies = root.GetProperty("_links").GetProperty("curies");
        Assert.Equal(JsonValueKind.Array, curies.ValueKind);
        Assert.Equal("acme", curies[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_curied_embedded_rel_surfaces_the_curie_in_links()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddLinks(new EmbeddedCurieLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/ce/{id:int}", (int id) => TypedResults.Ok(new CurieSectionOrder(id))).WithName("CeGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/ce/7")).RootElement;

        Assert.True(root.GetProperty("_embedded").TryGetProperty("acme:child", out _));

        var curies = root.GetProperty("_links").GetProperty("curies");
        Assert.Equal("acme", curies[0].GetProperty("name").GetString());
    }

    [Fact]
    public void AddCurie_requires_the_rel_variable_in_the_template()
    {
        var options = new CairnOptions();

        // A curie is advertised templated:true; without {rel} clients would expand nothing into it.
        var failure = Assert.Throws<ArgumentException>(() => options.AddCurie("acme", "https://docs.example.com/rels"));
        Assert.Contains("{rel}", failure.Message, StringComparison.Ordinal);

        options.AddCurie("acme", "https://docs.example.com/rels/{rel}");   // and the valid form still registers
    }

    [Fact]
    public async Task A_hal_response_omits_curies_used_only_by_affordance_names()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddLinks(new AffordanceCurieLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/ca/{id:int}", (int id) => TypedResults.Ok(new CurieSectionOrder(id))).WithName("CaGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/hal+json");

        var root = JsonDocument.Parse(await client.GetStringAsync("/ca/7")).RootElement;

        // HAL emits no affordances, so the acme prefix appears nowhere in the document — advertising its
        // curie would point at a relation the document doesn't carry.
        Assert.False(root.TryGetProperty("_actions", out _));
        Assert.False(root.GetProperty("_links").TryGetProperty("curies", out _));
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task A_hal_forms_response_omits_the_curie_of_a_sole_default_keyed_template()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddLinks(new AffordanceCurieLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/cf/{id:int}", (int id) => TypedResults.Ok(new CurieSectionOrder(id))).WithName("CfGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/cf/7")).RootElement;

        // The sole template emits under the reserved "default" key, so no acme:-prefixed key appears in the
        // document — a curie for the prefix would document a relation the document doesn't carry.
        Assert.True(root.GetProperty("_templates").TryGetProperty("default", out _));
        Assert.False(root.GetProperty("_templates").TryGetProperty("acme:reorder", out _));
        Assert.False(root.GetProperty("_links").TryGetProperty("curies", out _));
    }

    [Fact]
    public async Task A_hal_forms_response_omits_the_curie_of_an_AsDefault_affordance_but_keeps_named_ones()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddCurie("beta", "https://docs.example.com/beta/{rel}");
            o.AddLinks(new MixedDefaultCurieLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/cm/{id:int}", (int id) => TypedResults.Ok(new CurieSectionOrder(id))).WithName("CmGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/cm/7")).RootElement;

        // acme:approve is renamed to "default" on the wire, so only beta (used by the still-named
        // beta:reject template) is advertised.
        Assert.True(root.GetProperty("_templates").TryGetProperty("default", out _));
        Assert.True(root.GetProperty("_templates").TryGetProperty("beta:reject", out _));

        var curies = root.GetProperty("_links").GetProperty("curies");
        Assert.Equal(JsonValueKind.Array, curies.ValueKind);
        var names = curies.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("beta", names);
        Assert.DoesNotContain("acme", names);
    }

    private sealed record CurieSectionOrder(int Id)
    {
        public CurieSectionChild Child { get; } = new(Id);
    }

    private sealed record CurieSectionChild(int Id);

    private sealed class AffordanceCurieLinks : LinkConfig<CurieSectionOrder>
    {
        public override void Configure(ILinkBuilder<CurieSectionOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/ca/{o.Id}"));
            builder.Affordance("acme:reorder", o => LinkTarget.Uri($"/ca/{o.Id}/reorder")).Post();
        }
    }

    private sealed class EmbeddedCurieLinks : LinkConfig<CurieSectionOrder>
    {
        public override void Configure(ILinkBuilder<CurieSectionOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/ce/{o.Id}"));
            builder.Embed("acme:child", o => o.Child);
        }
    }

    private sealed class MixedDefaultCurieLinks : LinkConfig<CurieSectionOrder>
    {
        public override void Configure(ILinkBuilder<CurieSectionOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/cm/{o.Id}"));
            builder.Affordance("acme:approve", o => LinkTarget.Uri($"/cm/{o.Id}/approve")).Post().AsDefault();
            builder.Affordance("beta:reject", o => LinkTarget.Uri($"/cm/{o.Id}/reject")).Post();
        }
    }
}
