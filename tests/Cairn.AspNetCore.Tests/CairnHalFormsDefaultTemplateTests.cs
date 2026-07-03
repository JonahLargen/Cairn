using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    public async Task A_sole_template_is_keyed_default_even_without_AsDefault()
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

        // A response's only template is unambiguously the primary action: a generic HAL-FORMS client that
        // looks up the reserved key must find it even though the config never called AsDefault().
        Assert.True(templates.TryGetProperty("default", out var sole));
        Assert.Equal("PUT", sole.GetProperty("method").GetString());
        Assert.False(templates.TryGetProperty("update", out _));
    }

    [Fact]
    public async Task Multiple_unmarked_affordances_keep_their_named_template_keys()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new TwoNamedTemplateLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/nt2/{id:int}", (int id) => TypedResults.Ok(new DefaultTemplateOrder(id))).WithName("Nt2GetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var templates = JsonDocument.Parse(await client.GetStringAsync("/nt2/7")).RootElement.GetProperty("_templates");

        // With more than one template and nothing marked AsDefault, no one action is "the" default.
        Assert.True(templates.TryGetProperty("update", out _));
        Assert.True(templates.TryGetProperty("cancel", out _));
        Assert.False(templates.TryGetProperty("default", out _));
    }

    [Fact]
    public async Task Two_gated_defaults_emitting_together_log_a_collision_warning()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new CollidingDefaultLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/cd/{id:int}", (int id) => TypedResults.Ok(new DefaultTemplateOrder(id))).WithName("CdGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        // Both When()-gated AsDefault() affordances emit on this response — the conditions were supposed to
        // be mutually exclusive. The wire keeps the last claimant; the collision must be logged.
        var templates = JsonDocument.Parse(await client.GetStringAsync("/cd/7")).RootElement.GetProperty("_templates");
        Assert.True(templates.TryGetProperty("default", out _));

        await logs.WaitForAsync(m =>
            m.Contains("default", StringComparison.Ordinal)
            && m.Contains("approve", StringComparison.Ordinal)
            && m.Contains("reopen", StringComparison.Ordinal));
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

    private sealed class TwoNamedTemplateLinks : LinkConfig<DefaultTemplateOrder>
    {
        public override void Configure(ILinkBuilder<DefaultTemplateOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/nt2/{o.Id}"));
            builder.Affordance("update", o => LinkTarget.Uri($"/nt2/{o.Id}")).Put();
            builder.Affordance("cancel", o => LinkTarget.Uri($"/nt2/{o.Id}/cancel")).Delete();
        }
    }

    // Both defaults are gated, so registration accepts them; the gates are (deliberately) not mutually
    // exclusive, so both emit at runtime and collide on the reserved key.
    private sealed class CollidingDefaultLinks : LinkConfig<DefaultTemplateOrder>
    {
        public override void Configure(ILinkBuilder<DefaultTemplateOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/cd/{o.Id}"));
            builder.Affordance("approve", o => LinkTarget.Uri($"/cd/{o.Id}/approve")).Post().When(o => true).AsDefault();
            builder.Affordance("reopen", o => LinkTarget.Uri($"/cd/{o.Id}/reopen")).Post().When(o => true).AsDefault();
        }
    }
}
