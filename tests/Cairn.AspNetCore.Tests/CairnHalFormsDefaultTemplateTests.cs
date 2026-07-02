using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnHalFormsDefaultTemplateTests
{
    [Fact]
    public async Task An_affordance_marked_AsDefault_emits_under_the_reserved_default_template_key()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new DefaultTemplateLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/dt/{id:int}", (int id) => TypedResults.Ok(new DefaultTemplateOrder(id))).WithName("DtGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var templates = JsonDocument.Parse(await client.GetStringAsync("/dt/7")).RootElement.GetProperty("_templates");

        // The primary affordance lives under "default" (per HAL-FORMS); others keep their names.
        Assert.True(templates.TryGetProperty("default", out var primary));
        Assert.Equal("PUT", primary.GetProperty("method").GetString());
        Assert.True(templates.TryGetProperty("cancel", out _));
        Assert.False(templates.TryGetProperty("update", out _));
    }

    [Fact]
    public async Task Unmarked_affordances_keep_their_named_template_keys()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new NamedTemplateLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/nt/{id:int}", (int id) => TypedResults.Ok(new DefaultTemplateOrder(id))).WithName("NtGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var templates = JsonDocument.Parse(await client.GetStringAsync("/nt/7")).RootElement.GetProperty("_templates");

        // Nothing was marked AsDefault, so the wire format is unchanged.
        Assert.True(templates.TryGetProperty("update", out _));
        Assert.False(templates.TryGetProperty("default", out _));
    }

    [Fact]
    public async Task AsDefault_does_not_rename_the_action_in_the_default_format()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DefaultTemplateLinks()));

        await using var app = builder.Build();
        app.MapGet("/da/{id:int}", (int id) => TypedResults.Ok(new DefaultTemplateOrder(id))).WithName("DaGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var actions = JsonDocument.Parse(await client.GetStringAsync("/da/7")).RootElement.GetProperty("_actions");

        // _actions is Cairn's own shape, keyed by the affordance's name — AsDefault only affects _templates.
        Assert.True(actions.TryGetProperty("update", out _));
        Assert.False(actions.TryGetProperty("default", out _));
    }

    private sealed record DefaultTemplateOrder(int Id);

    private sealed class DefaultTemplateLinks : LinkConfig<DefaultTemplateOrder>
    {
        public override void Configure(ILinkBuilder<DefaultTemplateOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/dt/{o.Id}"));
            builder.Affordance("update", o => LinkTarget.Uri($"/dt/{o.Id}")).Put().AsDefault();
            builder.Affordance("cancel", o => LinkTarget.Uri($"/dt/{o.Id}/cancel")).Delete();
        }
    }

    private sealed class NamedTemplateLinks : LinkConfig<DefaultTemplateOrder>
    {
        public override void Configure(ILinkBuilder<DefaultTemplateOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/nt/{o.Id}"));
            builder.Affordance("update", o => LinkTarget.Uri($"/nt/{o.Id}")).Put();
        }
    }
}
