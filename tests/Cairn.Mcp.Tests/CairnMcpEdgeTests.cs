using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cairn.Mcp.Tests;

public class CairnMcpEdgeTests
{
    [Fact]
    public async Task An_affordance_under_the_default_policy_is_listed_only_for_authenticated_callers()
    {
        await using var app = await McpTestApp.StartAsync();

        await using (var anonymous = await McpTestApp.ConnectAsync(app))
        {
            Assert.DoesNotContain(await anonymous.ListToolsAsync(), t => t.Name == "order_note");
        }

        await using (var intern = await McpTestApp.ConnectAsync(app, role: "intern"))
        {
            Assert.Contains(await intern.ListToolsAsync(), t => t.Name == "order_note");
        }
    }

    [Fact]
    public async Task A_null_id_argument_is_an_error()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?> { ["id"] = null });

        Assert.True(result.IsError);
        Assert.Contains("'id' argument", TextOf(result));
    }

    [Fact]
    public async Task Calling_with_no_arguments_at_all_is_an_error_for_id_tools()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel");

        Assert.True(result.IsError);
        Assert.Contains("'id' argument", TextOf(result));
    }

    [Fact]
    public async Task A_singleton_resource_whose_loader_returns_null_reports_unavailable()
    {
        await using var app = await McpTestApp.StartAsync(
            options => options.AddResource<Ghost>("ghost", (_, _) => new ValueTask<Ghost?>((Ghost?)null)),
            configureCairn: options => options.AddLinks(new GhostLinks()));
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("ghost_get");

        Assert.True(result.IsError);
        Assert.Contains("ghost resource is not available", TextOf(result));
    }

    [Fact]
    public async Task A_resource_with_no_currently_available_actions_says_none()
    {
        await using var app = await McpTestApp.StartAsync(
            options => options.AddResource<Wisp>("wisp", (_, _) => new ValueTask<Wisp?>(new Wisp())),
            configureCairn: options => options.AddLinks(new WispLinks()));
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("wisp_vanish");

        Assert.True(result.IsError);
        Assert.Contains("Currently available actions: none", TextOf(result));
    }

    [Fact]
    public async Task Path_relative_link_urls_are_resolved_against_the_mcp_requests_host()
    {
        await using var app = await McpTestApp.StartAsync(
            configureCairn: options => options.UrlStyle = LinkUrlStyle.PathRelative);
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Cancelled", app.Services.GetRequiredService<OrderStore>().Find(1)!.Status);
    }

    [Fact]
    public async Task Get_query_values_cover_strings_numbers_booleans_and_nulls()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        var result = await client.CallToolAsync("order_audit", new Dictionary<string, object?>
        {
            ["id"] = "1",
            ["depth"] = 7,
            ["tag"] = "x",
            ["flag"] = true,
            ["note"] = null,
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("\"depth\":7", TextOf(result));
    }

    [Fact]
    public async Task A_serializer_without_a_contract_for_the_input_falls_back_to_reflection_names()
    {
        // A dedicated app with no JSON endpoints: a source-gen-only resolver that doesn't know the input type
        // would break minimal-API endpoint compilation, but the MCP schema derivation must still cope.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolver = McpTestJsonContext.Default;
            options.SerializerOptions.PropertyNamingPolicy = null;
        });
        builder.Services.AddCairn(options => options.AddLinks(new WidgetLinks()));
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithCairnAffordances(options =>
                options.AddResource<Widget>("widget", (_, _) => new ValueTask<Widget?>(new Widget(1))));

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var retag = (await client.ListToolsAsync()).Single(t => t.Name == "widget_retag");

        // The resolver has no contract for RetagRequest, so the schema is derived from reflection under the
        // options' (absent) naming policy: declared names, except where [JsonPropertyName] overrides.
        var properties = retag.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("Rush", out _));
        Assert.True(properties.TryGetProperty("custom_name", out _));
        Assert.False(properties.TryGetProperty("rush", out _));
    }

    [Fact]
    public async Task Registering_no_resources_yields_no_cairn_tools()
    {
        await using var app = await McpTestApp.StartAsync(
            _ => { },
            configureBuilder: builder => builder.Services.AddMcpServer().WithTools<EchoTool>());
        await using var client = await McpTestApp.ConnectAsync(app);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("echo", names);
        Assert.DoesNotContain(names, name => name.StartsWith("order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_link_config_provider_that_cannot_report_affordances_fails_with_guidance()
    {
        await using var app = await McpTestApp.StartAsync(configureBuilder: builder =>
            builder.Services.Replace(ServiceDescriptor.Singleton<ILinkConfigProvider>(new OpaqueProvider())));

        await Assert.ThrowsAnyAsync<Exception>(() => McpTestApp.ConnectAsync(app));
    }

    [Fact]
    public async Task Cairn_tools_coexist_with_tools_registered_through_the_sdk()
    {
        await using var app = await McpTestApp.StartAsync(configureBuilder: builder =>
            builder.Services.AddMcpServer().WithTools<EchoTool>());
        await using var client = await McpTestApp.ConnectAsync(app);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("echo", names);
        Assert.Contains("order_cancel", names);
    }

    [Fact]
    public void Repeated_registration_composes_instead_of_duplicating()
    {
        var services = new ServiceCollection();
        services.AddMcpServer()
            .WithCairnAffordances(_ => { })
            .WithCairnAffordances(_ => { });

        Assert.Single(services, s => s.ServiceType == typeof(ICairnMcpAffordanceInvoker));
    }

    [Theory]
    [InlineData(199, false)]
    [InlineData(204, true)]
    [InlineData(302, false)]
    public void Success_means_a_2xx_status(int status, bool success)
    {
        Assert.Equal(success, new CairnMcpAffordanceResult(status, null, null).IsSuccess);
    }

    [Fact]
    public async Task Calling_a_tool_without_cairn_registered_reports_the_misconfiguration()
    {
        // A hand-rolled ILinkConfigProvider satisfies tool setup, but the call needs the link engine AddCairn
        // registers — the failure should point at the missing registration rather than a null reference.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILinkConfigProvider>(new LinkConfigRegistry().Add(new WidgetLinks()));
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithCairnAffordances(options =>
                options.AddResource<Widget>("widget", (_, _) => new ValueTask<Widget?>(new Widget(1))));

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("widget_retag", new Dictionary<string, object?>());

        Assert.True(result.IsError);
    }

    [Fact]
    public void Resource_names_may_contain_underscores_and_dashes()
    {
        var options = new CairnMcpOptions();

        options.AddResource<Ghost>("spooky-Ghost_9", (_, _) => new ValueTask<Ghost?>(new Ghost()));
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    public sealed record Ghost;

    public sealed class GhostLinks : LinkConfig<Ghost>
    {
        public override void Configure(ILinkBuilder<Ghost> builder)
            => builder.Self(_ => LinkTarget.Uri("/ghost"));
    }

    public sealed record Wisp;

    public sealed class WispLinks : LinkConfig<Wisp>
    {
        public override void Configure(ILinkBuilder<Wisp> builder)
        {
            builder.Self(_ => LinkTarget.Uri("/wisp"));
            builder.Affordance("vanish", _ => LinkTarget.Uri("/wisp/vanish")).When(_ => false);
        }
    }

    private sealed class OpaqueProvider : ILinkConfigProvider
    {
        public ICompiledLinkConfig? GetConfig(Type resourceType) => new OpaqueConfig();

        private sealed class OpaqueConfig : ICompiledLinkConfig
        {
            public ValueTask<LinkSet> BuildAsync(object resource, LinkContext context, CancellationToken cancellationToken = default)
                => new(LinkSet.Empty);
        }
    }

    [McpServerToolType]
    public sealed class EchoTool
    {
        [McpServerTool(Name = "echo"), Description("Echoes its input.")]
        public static string Echo(string message) => message;
    }
}

[JsonSerializable(typeof(OrderDto))]
[JsonSerializable(typeof(OrdersResource))]
internal sealed partial class McpTestJsonContext : JsonSerializerContext;
