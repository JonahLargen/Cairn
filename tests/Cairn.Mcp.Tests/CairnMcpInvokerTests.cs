using ModelContextProtocol.Protocol;

namespace Cairn.Mcp.Tests;

public class CairnMcpInvokerTests
{
    [Fact]
    public async Task Get_affordances_send_their_inputs_as_query_parameters()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        var result = await client.CallToolAsync("order_audit", new Dictionary<string, object?>
        {
            ["id"] = "1",
            ["depth"] = 3,
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("\"depth\":3", TextOf(result));
    }

    [Fact]
    public async Task The_authorization_header_is_forwarded_to_the_endpoint_by_default()
    {
        await using var app = await McpTestApp.StartAsync();
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        // approve's endpoint itself requires the manager policy: it only answers 200 because the invoker
        // forwarded the MCP request's Authorization header.
        var result = await client.CallToolAsync("order_approve", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task Turning_off_header_forwarding_leaves_the_endpoint_unauthenticated()
    {
        await using var app = await McpTestApp.StartAsync(options =>
        {
            McpTestApp.Default(options);
            options.ForwardAuthorizationHeader = false;
        });
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        // The gate passes (the MCP request is authenticated), but the self-call carries no credentials, so
        // the endpoint's own authorization answers 401 — proof the endpoint stays authoritative.
        var result = await client.CallToolAsync("order_approve", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.True(result.IsError);
        Assert.Contains("HTTP 401", TextOf(result));
    }

    [Fact]
    public async Task The_invocation_request_can_be_customized()
    {
        await using var app = await McpTestApp.StartAsync(options =>
        {
            McpTestApp.Default(options);
            options.ConfigureInvocationRequest = (httpContext, request) =>
                request.Headers.TryAddWithoutValidation("X-Extra", "carried");
        });
        await using var client = await McpTestApp.ConnectAsync(app, role: "manager");

        var result = await client.CallToolAsync("order_audit", new Dictionary<string, object?> { ["id"] = "1" });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("\"echo\":\"carried\"", TextOf(result));
    }

    [Fact]
    public async Task A_non_json_content_type_is_rejected_by_the_default_invoker()
    {
        await using var app = await McpTestApp.StartAsync(
            options => options.AddResource<Upload>("upload", (_, _) => new ValueTask<Upload?>(new Upload(1))),
            configureCairn: options => options.AddLinks(new UploadLinks()));
        await using var client = await McpTestApp.ConnectAsync(app);

        var result = await client.CallToolAsync("upload_import", new Dictionary<string, object?>());

        Assert.True(result.IsError);
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}

public sealed record Upload(int Id);

public sealed class UploadLinks : LinkConfig<Upload>
{
    public override void Configure(ILinkBuilder<Upload> builder)
    {
        builder.Self(u => LinkTarget.Uri($"/uploads/{u.Id}"));
        builder.Affordance("import", u => LinkTarget.Uri($"/uploads/{u.Id}/import"))
            .Post()
            .ContentType("multipart/form-data");
    }
}
