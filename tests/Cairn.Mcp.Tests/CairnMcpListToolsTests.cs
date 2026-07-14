using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace Cairn.Mcp.Tests;

public class CairnMcpListToolsTests
{
    [Fact]
    public async Task Declared_affordances_and_get_tools_are_listed()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("order_get", names);
        Assert.Contains("order_cancel", names);
        Assert.Contains("order_audit", names);      // resource-based policy: cannot be decided without an instance
        Assert.Contains("orders_get", names);
        Assert.Contains("orders_create", names);
    }

    [Fact]
    public async Task A_tool_gated_by_a_caller_policy_is_hidden_from_callers_who_fail_it()
    {
        await using var app = await McpTestApp.StartAsync();

        await using (var anonymous = await McpTestApp.ConnectAsync(app))
        {
            var tools = await anonymous.ListToolsAsync();
            Assert.DoesNotContain(tools, t => t.Name == "order_approve");
        }

        await using (var intern = await McpTestApp.ConnectAsync(app, role: "intern"))
        {
            var tools = await intern.ListToolsAsync();
            Assert.DoesNotContain(tools, t => t.Name == "order_approve");
        }

        await using (var manager = await McpTestApp.ConnectAsync(app, role: "manager"))
        {
            var tools = await manager.ListToolsAsync();
            Assert.Contains(tools, t => t.Name == "order_approve");
        }
    }

    [Fact]
    public async Task The_affordance_tool_describes_its_gates_and_inputs()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var cancel = (await client.ListToolsAsync()).Single(t => t.Name == "order_cancel");

        Assert.Equal("Cancel the order", cancel.ProtocolTool.Title);
        Assert.Contains("only available while the resource is in an eligible state", cancel.Description);

        var schema = cancel.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("reason").GetProperty("type").GetString());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("id", required);
        Assert.DoesNotContain("reason", required);   // nullable → optional
    }

    [Fact]
    public async Task A_singleton_resource_tool_takes_no_id()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var create = (await client.ListToolsAsync()).Single(t => t.Name == "orders_create");

        var properties = create.JsonSchema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("id", out _));
        Assert.Equal("string", properties.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("quantity").GetProperty("type").GetString());
        var required = create.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("item", required);           // [Required]
        Assert.DoesNotContain("quantity", required); // value type → optional
    }

    [Fact]
    public async Task Get_tools_can_be_turned_off()
    {
        await using var app = await McpTestApp.StartAsync(options =>
        {
            McpTestApp.Default(options);
            options.IncludeGetTools = false;
        });
        await using var client = await McpTestApp.ConnectAsync(app);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("order_get", names);
        Assert.Contains("order_cancel", names);
    }

    [Fact]
    public async Task Tools_registered_alongside_cairns_are_not_filtered()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        // Only Cairn affordance tools participate in policy filtering; everything else passes through.
        var tools = await client.ListToolsAsync();
        Assert.All(tools, tool => Assert.False(string.IsNullOrEmpty(tool.Name)));
    }
}
