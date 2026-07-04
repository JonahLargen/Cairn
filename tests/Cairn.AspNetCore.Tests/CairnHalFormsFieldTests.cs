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
            .GetProperty("_templates").GetProperty("default");

        Assert.Equal("multipart/form-data", template.GetProperty("contentType").GetString());

        var props = template.GetProperty("properties").EnumerateArray().ToList();

        var status = props.Single(p => p.GetProperty("name").GetString() == "status");
        Assert.Equal("Order status", status.GetProperty("prompt").GetString());

        // Option values are what the default (numeric) enum binder accepts; names are the human prompts.
        var inline = status.GetProperty("options").GetProperty("inline").EnumerateArray()
            .Select(o => (Prompt: o.GetProperty("prompt").GetString(), Value: o.GetProperty("value").GetString()))
            .ToList();
        Assert.Contains(("Pending", "0"), inline);
        Assert.Contains(("Shipped", "1"), inline);

        var id = props.Single(p => p.GetProperty("name").GetString() == "id");
        Assert.True(id.GetProperty("readOnly").GetBoolean());

        var note = props.Single(p => p.GetProperty("name").GetString() == "note");
        Assert.Equal("Add a note", note.GetProperty("placeholder").GetString());
    }

    [Fact]
    public async Task Template_maps_datetime_default_value_and_required_semantics()
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

        var props = JsonDocument.Parse(await client.GetStringAsync("/orders/7")).RootElement
            .GetProperty("_templates").GetProperty("default").GetProperty("properties")
            .EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);

        // "datetime" is not a valid HAL-FORMS type; datetime-local is.
        Assert.Equal("datetime-local", props["scheduledAt"].GetProperty("type").GetString());

        // [DefaultValue] surfaces as the HAL-FORMS "value" key.
        Assert.Equal("3", props["priority"].GetProperty("value").GetString());

        // An enum default given as the enum member is emitted in the same wire form as its options (the
        // numeric "1" here), not the member name "Shipped" — otherwise it would preselect no option.
        Assert.Equal("1", props["defaultStatus"].GetProperty("value").GetString());
        var statusOptions = props["defaultStatus"].GetProperty("options").GetProperty("inline").EnumerateArray()
            .Select(o => o.GetProperty("value").GetString());
        Assert.Contains("1", statusOptions);

        // A default supplied as a raw number (not the enum member) keeps its plain formatting.
        Assert.Equal("1", props["numericStatus"].GetProperty("value").GetString());

        // The C# `required` modifier and a non-nullable reference type both mean required;
        // a nullable string does not.
        Assert.True(props["reference"].GetProperty("required").GetBoolean());
        Assert.False(props["note"].TryGetProperty("required", out _));
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

        public DateTime ScheduledAt { get; init; }

        [System.ComponentModel.DefaultValue(3)]
        public int Priority { get; init; }

        [System.ComponentModel.DefaultValue(OrderStatus.Shipped)]
        public OrderStatus DefaultStatus { get; init; }

        [System.ComponentModel.DefaultValue(1)]
        public OrderStatus NumericStatus { get; init; }

        public required string Reference { get; init; }
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
