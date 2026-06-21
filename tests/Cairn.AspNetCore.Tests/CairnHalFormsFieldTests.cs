using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnHalFormsFieldTests
{
    [Fact]
    public async Task Template_describes_prompt_options_readonly_placeholder_and_content_type()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new FormOrderLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new FormOrder(id))).WithName("FormGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/update", (int id) => TypedResults.NoContent()).WithName("FormUpdate");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var template = JsonDocument.Parse(await client.GetStringAsync("/orders/7")).RootElement
            .GetProperty("_templates").GetProperty("update");

        Assert.Equal("multipart/form-data", template.GetProperty("contentType").GetString());

        var props = template.GetProperty("properties").EnumerateArray().ToList();

        var status = props.Single(p => p.GetProperty("name").GetString() == "status");
        Assert.Equal("Order status", status.GetProperty("prompt").GetString());
        var inline = status.GetProperty("options").GetProperty("inline").EnumerateArray()
            .Select(o => o.GetProperty("value").GetString()).ToList();
        Assert.Contains("Pending", inline);
        Assert.Contains("Shipped", inline);

        var id = props.Single(p => p.GetProperty("name").GetString() == "id");
        Assert.True(id.GetProperty("readOnly").GetBoolean());

        var note = props.Single(p => p.GetProperty("name").GetString() == "note");
        Assert.Equal("Add a note", note.GetProperty("placeholder").GetString());
    }

    private sealed record FormOrder(int Id);

    private enum OrderStatus
    {
        Pending,
        Shipped,
        Cancelled,
    }

    private sealed class FormUpdateInput
    {
        [Editable(false)]
        public int Id { get; init; }

        [Display(Name = "Order status")]
        public OrderStatus Status { get; init; }

        [Display(Prompt = "Add a note")]
        public string? Note { get; init; }
    }

    private sealed class FormOrderLinks : LinkConfig<FormOrder>
    {
        public override void Configure(ILinkBuilder<FormOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("FormGetOrder", new { id = order.Id }));
            builder.Affordance("update", order => LinkTarget.Route("FormUpdate", new { id = order.Id }))
                .Accepts<FormUpdateInput>()
                .ContentType("multipart/form-data");
        }
    }
}
