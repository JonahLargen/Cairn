using Microsoft.Extensions.DependencyInjection;

namespace Cairn.Mcp.Tests;

public class CairnMcpOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("or der")]
    [InlineData("order/x")]
    public void An_invalid_resource_name_is_rejected(string? name)
    {
        var options = new CairnMcpOptions();

        Assert.Throws<ArgumentException>(() =>
            options.AddResource<OrderDto>(name!, (_, _, _) => new ValueTask<OrderDto?>((OrderDto?)null)));
    }

    [Fact]
    public void Null_loaders_are_rejected()
    {
        var options = new CairnMcpOptions();

        Assert.Throws<ArgumentNullException>(() =>
            options.AddResource<OrderDto>("order", (Func<string, IServiceProvider, CancellationToken, ValueTask<OrderDto?>>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            options.AddResource<OrdersResource>("orders", (Func<IServiceProvider, CancellationToken, ValueTask<OrdersResource?>>)null!));
    }

    [Fact]
    public void The_builder_extension_validates_its_arguments()
    {
        var builder = new ServiceCollection().AddMcpServer();

        Assert.Throws<ArgumentNullException>(() => CairnMcpServerBuilderExtensions.WithCairnAffordances(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => builder.WithCairnAffordances(null!));
    }

    [Fact]
    public async Task A_resource_without_a_link_configuration_fails_the_first_request_with_guidance()
    {
        await using var app = await McpTestApp.StartAsync(options =>
            options.AddResource<Unconfigured>("mystery", (_, _) => new ValueTask<Unconfigured?>(new Unconfigured())));

        await Assert.ThrowsAnyAsync<Exception>(() => McpTestApp.ConnectAsync(app));
    }

    [Fact]
    public async Task An_affordance_named_get_collides_with_the_state_inspection_tool()
    {
        await using var app = await McpTestApp.StartAsync(
            options => options.AddResource<Gadget>("gadget", (_, _) => new ValueTask<Gadget?>(new Gadget(1))),
            configureCairn: options => options.AddLinks(new GadgetLinks()));

        await Assert.ThrowsAnyAsync<Exception>(() => McpTestApp.ConnectAsync(app));
    }

    [Fact]
    public async Task Registering_the_same_resource_twice_collides()
    {
        await using var app = await McpTestApp.StartAsync(options =>
        {
            McpTestApp.Default(options);
            McpTestApp.Default(options);
        });

        await Assert.ThrowsAnyAsync<Exception>(() => McpTestApp.ConnectAsync(app));
    }

    [Fact]
    public async Task Tool_names_sanitize_characters_mcp_disallows()
    {
        // A curie-style relation like "acme:archive:v2" is legal hypermedia but not a legal tool name; dashes
        // and underscores pass through untouched.
        await using var app = await McpTestApp.StartAsync(
            options => options.AddResource<Gizmo>("giz-mo", (_, _) => new ValueTask<Gizmo?>(new Gizmo(1))),
            configureCairn: options => options.AddLinks(new GizmoLinks()));
        await using var client = await McpTestApp.ConnectAsync(app);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, t => t.Name == "giz-mo_acme-archive-v2");
    }

    public sealed record Unconfigured;

    public sealed record Gadget(int Id);

    public sealed class GadgetLinks : LinkConfig<Gadget>
    {
        public override void Configure(ILinkBuilder<Gadget> builder)
        {
            builder.Self(g => LinkTarget.Uri($"/gadgets/{g.Id}"));
            builder.Affordance("get", g => LinkTarget.Uri($"/gadgets/{g.Id}/refresh")).Get();
        }
    }

    public sealed record Gizmo(int Id);

    public sealed class GizmoLinks : LinkConfig<Gizmo>
    {
        public override void Configure(ILinkBuilder<Gizmo> builder)
        {
            builder.Self(g => LinkTarget.Uri($"/gizmos/{g.Id}"));
            builder.Affordance("acme:archive:v2", g => LinkTarget.Uri($"/gizmos/{g.Id}/archive")).Post();
        }
    }
}
