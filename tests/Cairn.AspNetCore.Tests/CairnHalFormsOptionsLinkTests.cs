using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Options by reference: a [HalFormsOptionsLink] property emits an options.link the way the client parses it
// (into AffordanceField.OptionsLink), instead of an inline enumeration.
public class CairnHalFormsOptionsLinkTests
{
    [Fact]
    public async Task A_referenced_field_emits_options_link_and_no_inline_list()
    {
        var props = await TemplatePropertiesAsync<OptionsLinkInput>();

        var options = props["tag"].GetProperty("options");
        Assert.Equal("/tags", options.GetProperty("link").GetProperty("href").GetString());

        // A link-sourced options block carries no inline list, and omits the optional link/field metadata.
        Assert.False(options.TryGetProperty("inline", out _));
        Assert.False(options.GetProperty("link").TryGetProperty("templated", out _));
        Assert.False(options.GetProperty("link").TryGetProperty("type", out _));
        Assert.False(options.TryGetProperty("promptField", out _));
        Assert.False(options.TryGetProperty("valueField", out _));
    }

    [Fact]
    public async Task A_referenced_field_carries_templated_type_and_field_mappings_when_declared()
    {
        var props = await TemplatePropertiesAsync<OptionsLinkInput>();

        var options = props["assignee"].GetProperty("options");
        var link = options.GetProperty("link");
        Assert.Equal("/users{?q}", link.GetProperty("href").GetString());
        Assert.True(link.GetProperty("templated").GetBoolean());
        Assert.Equal("application/json", link.GetProperty("type").GetString());
        Assert.Equal("name", options.GetProperty("promptField").GetString());
        Assert.Equal("id", options.GetProperty("valueField").GetString());
    }

    [Fact]
    public async Task The_reference_overrides_the_inline_derivation_of_an_enum_field()
    {
        var props = await TemplatePropertiesAsync<OptionsLinkInput>();

        // status is an enum, which would otherwise derive an inline list; the attribute points it at a link
        // instead, and HAL-FORMS options carry one source or the other, never both.
        var options = props["status"].GetProperty("options");
        Assert.Equal("/statuses", options.GetProperty("link").GetProperty("href").GetString());
        Assert.False(options.TryGetProperty("inline", out _));
    }

    [Fact]
    public async Task The_client_reads_a_server_emitted_options_link()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new OptionsLinkTicketLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/tickets/{id:int}", (int id) => TypedResults.Ok(new OptionsLinkTicket(id))).WithName("OptionsLinkGetTicket").WithLinks();
        app.MapPost("/tickets/{id:int}/assign", (int id) => TypedResults.NoContent()).WithName("OptionsLinkAssign");

        await app.StartAsync();
        using var httpClient = app.GetTestClient();

        var ticket = (await new CairnClient(httpClient).GetAsync<OptionsLinkTicket>("/tickets/7")).EnsureSuccess();

        var assignee = ticket.Fields("default").Single(f => f.Name == "assignee");
        Assert.Equal("/users{?q}", assignee.OptionsLink);

        // The remote list is the value source, so no inline options are parsed for the field.
        Assert.Empty(assignee.Options);
    }

    private static async Task<Dictionary<string, JsonElement>> TemplatePropertiesAsync<TInput>()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new OptionsLinkLinks<TInput>());
        });

        await using var app = builder.Build();
        app.MapGet("/doc/{id:int}", (int id) => TypedResults.Ok(new OptionsLinkDoc(id))).WithName("OptionsLinkGet").WithLinks();
        app.MapPost("/doc/{id:int}/edit", (int id) => TypedResults.NoContent()).WithName("OptionsLinkEdit");

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/doc/1")).RootElement
            .GetProperty("_templates").GetProperty("default").GetProperty("properties")
            .EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
    }

    private enum TicketStatus
    {
        Open,
        Closed,
    }

    private sealed class OptionsLinkInput
    {
        [HalFormsOptionsLink("/tags")]
        public string Tag { get; init; } = "";

        [HalFormsOptionsLink("/users{?q}", Templated = true, Type = "application/json", ValueField = "id", PromptField = "name")]
        public string Assignee { get; init; } = "";

        [HalFormsOptionsLink("/statuses")]
        public TicketStatus Status { get; init; }
    }

    private sealed record OptionsLinkDoc(int Id);

    private sealed class OptionsLinkLinks<TInput> : LinkConfig<OptionsLinkDoc>
    {
        public override void Configure(ILinkBuilder<OptionsLinkDoc> builder)
        {
            builder.Self(doc => LinkTarget.Route("OptionsLinkGet", new { id = doc.Id }));
            builder.Affordance("edit", doc => LinkTarget.Route("OptionsLinkEdit", new { id = doc.Id }))
                .Put()
                .Accepts<TInput>();
        }
    }

    private sealed record OptionsLinkTicket(int Id);

    private sealed class OptionsLinkAssignInput
    {
        [HalFormsOptionsLink("/users{?q}", Templated = true)]
        public string Assignee { get; init; } = "";
    }

    private sealed class OptionsLinkTicketLinks : LinkConfig<OptionsLinkTicket>
    {
        public override void Configure(ILinkBuilder<OptionsLinkTicket> builder)
        {
            builder.Self(ticket => LinkTarget.Route("OptionsLinkGetTicket", new { id = ticket.Id }));
            builder.Affordance("assign", ticket => LinkTarget.Route("OptionsLinkAssign", new { id = ticket.Id }))
                .Post()
                .Accepts<OptionsLinkAssignInput>();
        }
    }
}
