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
    public async Task A_whitespace_id_argument_is_an_error()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?> { ["id"] = "   " });

        Assert.True(result.IsError);
        Assert.Contains("'id' argument", TextOf(result));
    }

    [Fact]
    public async Task An_available_affordance_called_with_no_arguments_still_reaches_its_endpoint()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        // orders_create is always advertised; with no arguments at all the invoker submits an empty JSON
        // object and the endpoint answers for itself — surfaced as a tool error carrying the status.
        var result = await client.CallToolAsync("orders_create");

        Assert.True(result.IsError);
        Assert.Contains("failed with HTTP 400", TextOf(result));
    }

    [Fact]
    public async Task Forgetting_add_cairn_entirely_fails_the_first_request_with_guidance()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithCairnAffordances(options =>
                options.AddResource<Widget>("widget", (_, _) => new ValueTask<Widget?>(new Widget(1))));

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => McpTestApp.ConnectAsync(app));
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
    public async Task The_reflection_fallback_honors_the_options_naming_policy()
    {
        // Same source-gen-only resolver, but with the default camelCase policy left in place: fallback names
        // go through the policy instead of keeping their declared casing.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolver = McpTestJsonContext.Default);
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

        Assert.True(retag.JsonSchema.GetProperty("properties").TryGetProperty("rush", out _));
    }

    [Fact]
    public async Task A_custom_list_handler_returning_no_tools_passes_through_the_filter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] }))
            .WithCairnAffordances(_ => { });

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        Assert.Empty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task A_custom_list_handler_without_a_tool_collection_passes_through_the_filter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => new ValueTask<ListToolsResult>(new ListToolsResult
            {
                Tools = [new Tool { Name = "phantom" }],
            }))
            .WithCairnAffordances(_ => { });

        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        Assert.Contains(await client.ListToolsAsync(), t => t.Name == "phantom");
    }

    [Fact]
    public async Task Tools_refuse_a_transport_that_supplies_no_service_provider()
    {
        var tool = BuildWidgetTool();
        var context = new RequestContext<CallToolRequestParams>(new FakeMcpServer(), ToolCallRequest(), new CallToolRequestParams { Name = "widget_retag" })
        {
            Services = null,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(context).AsTask());

        Assert.Contains("no service provider", exception.Message);
    }

    [Fact]
    public async Task Tools_refuse_to_run_outside_an_http_request()
    {
        var tool = BuildWidgetTool();

        // No IHttpContextAccessor registered at all…
        await using var bare = new ServiceCollection().BuildServiceProvider();
        var withoutAccessor = new RequestContext<CallToolRequestParams>(new FakeMcpServer(), ToolCallRequest(), new CallToolRequestParams { Name = "widget_retag" })
        {
            Services = bare,
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(withoutAccessor).AsTask());
        Assert.Contains("No HTTP request is active", exception.Message);

        // …and an accessor with no active request both fail the same way.
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        await using var idle = services.BuildServiceProvider();
        var withoutRequest = new RequestContext<CallToolRequestParams>(new FakeMcpServer(), ToolCallRequest(), new CallToolRequestParams { Name = "widget_retag" })
        {
            Services = idle,
        };
        exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(withoutRequest).AsTask());
        Assert.Contains("No HTTP request is active", exception.Message);
    }

    private static ModelContextProtocol.Server.McpServerTool BuildWidgetTool()
    {
        var services = new ServiceCollection();
        services.AddCairn(options => options.AddLinks(new WidgetLinks()));
        services.AddMcpServer().WithCairnAffordances(options =>
            options.AddResource<Widget>("widget", (_, _) => new ValueTask<Widget?>(new Widget(1))));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        Assert.True(options.ToolCollection!.TryGetPrimitive("widget_retag", out var tool));
        return tool;
    }

    private static ModelContextProtocol.Protocol.JsonRpcRequest ToolCallRequest()
        => new() { Method = "tools/call" };

    /// <summary>The minimal <see cref="McpServer"/> stand-in its protected constructor exists for.</summary>
#pragma warning disable MCPEXP002 // The protected McpServer constructor is experimental; it exists for exactly this kind of test double.
    private sealed class FakeMcpServer : McpServer
    {
        public override ClientCapabilities ClientCapabilities => new();

        public override Implementation ClientInfo => new() { Name = "test", Version = "0" };

        public override McpServerOptions ServerOptions => new();

        public override IServiceProvider Services => EmptyProvider.Instance;

        public override ModelContextProtocol.Protocol.LoggingLevel? LoggingLevel => null;

        public override string? SessionId => null;

        public override string? NegotiatedProtocolVersion => null;

        public override Task RunAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task SendMessageAsync(ModelContextProtocol.Protocol.JsonRpcMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public override Task<ModelContextProtocol.Protocol.JsonRpcResponse> SendRequestAsync(ModelContextProtocol.Protocol.JsonRpcRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override IAsyncDisposable RegisterNotificationHandler(string method, Func<ModelContextProtocol.Protocol.JsonRpcNotification, CancellationToken, ValueTask> handler)
            => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
#pragma warning restore MCPEXP002

        private sealed class EmptyProvider : IServiceProvider
        {
            public static readonly EmptyProvider Instance = new();

            public object? GetService(Type serviceType) => null;
        }
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
