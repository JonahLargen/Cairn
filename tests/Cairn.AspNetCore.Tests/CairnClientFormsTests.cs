using System.ComponentModel.DataAnnotations;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnClientFormsTests
{
    [Fact]
    public async Task Reads_affordance_input_fields_from_a_hal_forms_template()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new FieldOrderLinks());
            o.DefaultFormat = HypermediaFormat.HalForms;   // emit _templates with properties
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new FieldOrder(id))).WithName("FieldGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("FieldCancel");

        await app.StartAsync();
        using var httpClient = app.GetTestClient();

        var order = (await new CairnClient(httpClient).GetAsync<FieldOrder>("/orders/42")).EnsureSuccess();
        var fields = order.Fields("cancel");

        var reason = fields.Single(f => f.Name == "reason");
        Assert.True(reason.Required);
        Assert.Equal("text", reason.Type);
        Assert.Equal(200, reason.MaxLength);

        var severity = fields.Single(f => f.Name == "severity");
        Assert.Equal("number", severity.Type);
        Assert.Equal(1d, severity.Min);
        Assert.Equal(5d, severity.Max);
        Assert.False(severity.Required);
    }

    [Fact]
    public async Task An_affordance_without_a_template_reports_no_fields()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // Pin the Default format (_actions, no properties): the client's Accept would otherwise negotiate
        // HAL-FORMS and receive a template with fields.
        builder.Services.AddCairn(o =>
        {
            o.NegotiateFormat = false;
            o.AddLinks(new FieldOrderLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new FieldOrder(id))).WithName("FieldGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("FieldCancel");

        await app.StartAsync();
        using var httpClient = app.GetTestClient();

        var order = (await new CairnClient(httpClient).GetAsync<FieldOrder>("/orders/42")).EnsureSuccess();

        Assert.True(order.HasAffordance("cancel"));
        Assert.Empty(order.Fields("cancel"));
    }

    private sealed record FieldOrder(int Id);

    private sealed class CancelRequest
    {
        [Required]
        [StringLength(200)]
        public string Reason { get; init; } = "";

        [Range(1, 5)]
        public int Severity { get; init; }
    }

    private sealed class FieldOrderLinks : LinkConfig<FieldOrder>
    {
        public override void Configure(ILinkBuilder<FieldOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("FieldGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("FieldCancel", new { id = order.Id }))
                .Method("POST")
                .Accepts<CancelRequest>();
        }
    }
}
