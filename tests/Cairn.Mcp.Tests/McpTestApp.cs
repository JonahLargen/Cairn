using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cairn.Mcp.Tests;

/// <summary>
/// Builds the in-memory API the tests run against: an order resource with state- and policy-gated
/// affordances, an orders singleton with a create form, a header-driven test authentication scheme, and an
/// MCP endpoint whose default invoker is routed back through the TestServer.
/// </summary>
internal static class McpTestApp
{
    public static async Task<WebApplication> StartAsync(
        Action<CairnMcpOptions>? configureMcp = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<AspNetCore.CairnOptions>? configureCairn = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton<OrderStore>();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("manager", policy => policy.RequireClaim("role", "manager")));

        builder.Services.AddCairn(options =>
        {
            options.AddLinks(new OrderLinks());
            options.AddLinks(new OrdersLinks());
            configureCairn?.Invoke(options);
        });

        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithCairnAffordances(configureMcp ?? Default);

        // Route the default invoker's self-call through the in-memory server.
        builder.Services.AddHttpClient("Cairn.Mcp")
            .ConfigurePrimaryHttpMessageHandler(services => ((TestServer)services.GetRequiredService<IServer>()).CreateHandler());

        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/orders/{id:int}", (int id, OrderStore store) =>
            store.Find(id) is { } order ? Results.Ok(order) : Results.NotFound())
            .WithName("get-order")
            .WithLinks();

        app.MapPost("/orders/{id:int}/cancel", (int id, CancelOrderRequest? request, OrderStore store) =>
        {
            if (store.Find(id) is not { } order || order.Status != "Pending")
            {
                return Results.Conflict();
            }

            store.Save(order with { Status = "Cancelled" });
            store.CancelReasons[id] = request?.Reason;
            return Results.NoContent();
        }).WithName("cancel-order");

        app.MapPost("/orders/{id:int}/approve", (int id) => Results.Ok(new { approved = id }))
            .WithName("approve-order")
            .RequireAuthorization("manager");

        app.MapGet("/orders/{id:int}/audit", (int id, int? depth, HttpContext http) =>
            Results.Ok(new { id, depth, echo = http.Request.Headers["X-Extra"].ToString() }))
            .WithName("audit-order");

        app.MapPost("/orders/{id:int}/reject", (int id) => Results.BadRequest("rejections are closed"))
            .WithName("reject-order");

        app.MapGet("/orders", (OrderStore store) => new OrdersResource(store.Count))
            .WithName("list-orders")
            .WithLinks();

        app.MapPost("/orders", (CreateOrderRequest request, OrderStore store) =>
        {
            if (string.IsNullOrEmpty(request.Item))
            {
                return Results.BadRequest("item is required");
            }

            var order = store.Create(request.Item);
            return Results.Created($"/orders/{order.Id}", order);
        }).WithName("create-order");

        app.MapMcp("/mcp");

        await app.StartAsync();
        return app;
    }

    public static void Default(CairnMcpOptions options)
    {
        options.AddResource<OrderDto>("order", async (id, services, cancellationToken) =>
        {
            await Task.Yield();
            return int.TryParse(id, out var parsed) ? services.GetRequiredService<OrderStore>().Find(parsed) : null;
        });
        options.AddResource<OrdersResource>("orders", (services, _) =>
            new ValueTask<OrdersResource?>(new OrdersResource(services.GetRequiredService<OrderStore>().Count)));
    }

    /// <summary>Connects an MCP client through the TestServer, optionally authenticating as <paramref name="role"/>.</summary>
    public static async Task<McpClient> ConnectAsync(WebApplication app, string? role = null)
    {
        var http = app.GetTestClient();
        if (role is not null)
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Test {role}");
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http,
            loggerFactory: null,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport);
    }
}

public sealed record OrderDto(int Id, string Status, decimal Total);

public sealed record OrdersResource(int Count);

public sealed record CancelOrderRequest(string? Reason);

public sealed record CreateOrderRequest([property: Required] string Item, int Quantity);

public sealed class OrderStore
{
    private readonly ConcurrentDictionary<int, OrderDto> _orders = new()
    {
        [1] = new OrderDto(1, "Pending", 12.5m),
        [2] = new OrderDto(2, "Shipped", 99m),
    };

    public ConcurrentDictionary<int, string?> CancelReasons { get; } = new();

    public int Count => _orders.Count;

    public OrderDto? Find(int id) => _orders.TryGetValue(id, out var order) ? order : null;

    public void Save(OrderDto order) => _orders[order.Id] = order;

    public OrderDto Create(string item)
    {
        var order = new OrderDto(_orders.Keys.Max() + 1, "Pending", item.Length);
        _orders[order.Id] = order;
        return order;
    }
}

public sealed class OrderLinks : LinkConfig<OrderDto>
{
    public override void Configure(ILinkBuilder<OrderDto> builder)
    {
        builder.Self(o => LinkTarget.Route("get-order", new { id = o.Id })).Title("The order");
        builder.Affordance("cancel", o => LinkTarget.Route("cancel-order", new { id = o.Id }))
            .Post()
            .Accepts<CancelOrderRequest>()
            .Title("Cancel the order")
            .When(o => o.Status == "Pending");
        builder.Affordance("approve", o => LinkTarget.Route("approve-order", new { id = o.Id }))
            .Post()
            .RequireAuthorization("manager");
        builder.Affordance("audit", o => LinkTarget.Route("audit-order", new { id = o.Id }))
            .Get()
            .RequireAuthorization("manager", o => o);
        builder.Affordance("reject", o => LinkTarget.Route("reject-order", new { id = o.Id }))
            .Post();
        builder.Affordance("note", o => LinkTarget.Route("audit-order", new { id = o.Id }))
            .Get()
            .RequireAuthorization();   // the host's default policy (an authenticated caller)
    }
}

public sealed class OrdersLinks : LinkConfig<OrdersResource>
{
    public override void Configure(ILinkBuilder<OrdersResource> builder)
    {
        builder.Self(_ => LinkTarget.Route("list-orders"));
        builder.Affordance("create", _ => LinkTarget.Route("create-order"))
            .Post()
            .Accepts<CreateOrderRequest>()
            .ContentType("application/json");
    }
}

/// <summary>Authenticates <c>Authorization: Test {role}</c> as a user holding that <c>role</c> claim.</summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Test ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var role = header["Test ".Length..];
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, role), new Claim("role", role)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
