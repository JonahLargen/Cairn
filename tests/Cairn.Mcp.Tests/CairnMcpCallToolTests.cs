using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Cairn.Mcp.Tests;

public class CairnMcpCallToolTests
{
    [Fact]
    public async Task An_available_affordance_is_invoked_against_its_own_endpoint()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?>
        {
            ["id"] = "1",
            ["reason"] = "changed my mind",
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("HTTP 204", TextOf(result));

        var store = app.Services.GetRequiredService<OrderStore>();
        Assert.Equal("Cancelled", store.Find(1)!.Status);
        Assert.Equal("changed my mind", store.CancelReasons[1]);   // the input body reached the endpoint
    }

    [Fact]
    public async Task A_state_gated_affordance_reports_unavailable_instead_of_invoking()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        // Order 2 is Shipped: the cancel gate fails even though the endpoint exists.
        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?> { ["id"] = "2" });

        Assert.True(result.IsError);
        var text = TextOf(result);
        Assert.Contains("'cancel' action is not currently available", text);
        Assert.Contains("'approve'", text);   // the manager still sees what it could do instead
        Assert.Equal("Shipped", app.Services.GetRequiredService<OrderStore>().Find(2)!.Status);
    }

    [Fact]
    public async Task An_auth_gated_affordance_is_refused_for_a_caller_who_fails_the_policy()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_approve", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.True(result.IsError);
        Assert.Contains("not currently available", TextOf(result));
    }

    [Fact]
    public async Task An_auth_gated_affordance_succeeds_for_an_authorized_caller()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        var result = await client.CallToolAsync("order_approve", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("\"approved\":1", TextOf(result));
    }

    [Fact]
    public async Task A_resource_based_policy_is_enforced_at_call_time()
    {
        await using var app = await McpTestApp.StartAsync();

        await using (var anonymous = await McpTestApp.ConnectAsync(app))
        {
            var result = await anonymous.CallToolAsync("order_audit", new Dictionary<string, object?> { ["id"] = "1" });
            Assert.True(result.IsError);
        }

        await using (var manager = await McpTestApp.ConnectAsync(app, role: "manager"))
        {
            var result = await manager.CallToolAsync("order_audit", new Dictionary<string, object?> { ["id"] = "1" });
            Assert.NotEqual(true, result.IsError);
        }
    }

    [Fact]
    public async Task A_missing_id_argument_is_an_error()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Contains("'id' argument", TextOf(result));
    }

    [Fact]
    public async Task An_unknown_id_is_an_error()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?> { ["id"] = "999" });

        Assert.True(result.IsError);
        Assert.Contains("No order with id '999'", TextOf(result));
    }

    [Fact]
    public async Task A_numeric_id_argument_is_accepted_as_its_raw_text()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("order_cancel", new Dictionary<string, object?> { ["id"] = 1 });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Cancelled", app.Services.GetRequiredService<OrderStore>().Find(1)!.Status);
    }

    [Fact]
    public async Task The_get_tool_returns_state_links_and_currently_available_actions()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        var result = await client.CallToolAsync("order_get", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(TextOf(result));
        var root = payload.RootElement;

        Assert.Equal("Pending", root.GetProperty("resource").GetProperty("status").GetString());
        Assert.Contains(
            root.GetProperty("links").EnumerateArray(),
            link => link.GetProperty("rel").GetString() == "self" && link.GetProperty("href").GetString()!.EndsWith("/orders/1", StringComparison.Ordinal));

        var actions = root.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("cancel", actions);
        Assert.Contains("approve", actions);   // the manager passes the policy gate
    }

    [Fact]
    public async Task The_get_tool_reflects_gating_for_the_caller_and_state()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        // Order 2 is Shipped and the caller is anonymous: only the ungated 'reject' action remains — cancel
        // (state), approve (caller policy), and audit (resource policy) are all gated off.
        var result = await client.CallToolAsync("order_get", new Dictionary<string, object?> { ["id"] = "2" });

        using var payload = JsonDocument.Parse(TextOf(result));
        var actions = payload.RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Equal(["reject"], actions);
    }

    [Fact]
    public async Task A_singleton_resource_invokes_without_an_id()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("orders_create", new Dictionary<string, object?>
        {
            ["item"] = "compass",
            ["quantity"] = 2,
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("compass".Length.ToString(System.Globalization.CultureInfo.InvariantCulture), TextOf(result));
        Assert.Equal(3, app.Services.GetRequiredService<OrderStore>().Count);
    }

    [Fact]
    public async Task An_endpoint_failure_surfaces_as_a_tool_error_with_the_status()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app);

        // The 'reject' affordance is always advertised, but its endpoint always answers 400: the endpoint
        // stays authoritative, and the failure comes back as a tool error carrying status and body.
        var result = await client.CallToolAsync("order_reject", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.True(result.IsError);
        var text = TextOf(result);
        Assert.Contains("failed with HTTP 400", text);
        Assert.Contains("rejections are closed", text);
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
