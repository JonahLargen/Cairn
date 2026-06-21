using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnMvcTests
{
    [Fact]
    public async Task Injects_self_link_into_a_controller_action_result()
    {
        await using var app = await StartAsync();

        var root = await GetJsonAsync(app.Client, "/mvc/orders/42");

        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.EndsWith("/mvc/orders/42", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Injects_state_conditional_affordance_with_resolved_route()
    {
        await using var app = await StartAsync();

        var cancel = (await GetJsonAsync(app.Client, "/mvc/orders/42")).GetProperty("_actions").GetProperty("cancel");

        Assert.Equal("POST", cancel.GetProperty("method").GetString());
        Assert.EndsWith("/mvc/orders/42/cancel", cancel.GetProperty("href").GetString());
    }

    [Fact]
    public async Task Omits_affordance_when_state_condition_fails()
    {
        await using var app = await StartAsync();

        var root = await GetJsonAsync(app.Client, "/mvc/orders/shipped/7");

        Assert.True(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Links_the_value_of_an_action_result()
    {
        await using var app = await StartAsync();

        var root = await GetJsonAsync(app.Client, "/mvc/orders/find/7");

        Assert.EndsWith("/mvc/orders/7", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Returns_not_found_for_the_other_action_result_branch()
    {
        await using var app = await StartAsync();

        var response = await app.Client.GetAsync("/mvc/orders/find/0");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Links_each_item_in_a_returned_collection()
    {
        await using var app = await StartAsync();

        var items = (await GetJsonAsync(app.Client, "/mvc/orders")).EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.EndsWith("/mvc/orders/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("/mvc/orders/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.True(items[0].TryGetProperty("_actions", out _));    // item 1 is Pending
        Assert.False(items[1].TryGetProperty("_actions", out _));   // item 2 is Shipped
    }

    [Fact]
    public async Task Paged_envelope_gets_pagination_links_and_links_each_item()
    {
        await using var app = await StartAsync();

        var root = await GetJsonAsync(app.Client, "/mvc/orders/paged?page=2");
        var links = root.GetProperty("_links");

        Assert.EndsWith("page=2", links.GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("page=1", links.GetProperty("first").GetProperty("href").GetString());
        Assert.EndsWith("page=1", links.GetProperty("prev").GetProperty("href").GetString());
        Assert.EndsWith("page=3", links.GetProperty("next").GetProperty("href").GetString());
        Assert.EndsWith("page=3", links.GetProperty("last").GetProperty("href").GetString());

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.EndsWith("/mvc/orders/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Cursor_envelope_emits_self_next_prev_and_links_items()
    {
        await using var app = await StartAsync();

        var root = await GetJsonAsync(app.Client, "/mvc/orders/cursor");
        var links = root.GetProperty("_links");

        Assert.True(links.TryGetProperty("self", out _));
        Assert.EndsWith("cursor=next-cur", links.GetProperty("next").GetProperty("href").GetString());
        Assert.EndsWith("cursor=prev-cur", links.GetProperty("prev").GetProperty("href").GetString());
        Assert.False(root.TryGetProperty("next", out _));   // raw cursors are not leaked as data

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.EndsWith("/mvc/orders/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Custom_paging_envelope_via_AddPaging_links_items()
    {
        await using var app = await StartAsync();

        var root = await GetJsonAsync(app.Client, "/mvc/orders/custom");

        Assert.EndsWith("page=2", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        var records = root.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(2, records.Count);
        Assert.EndsWith("/mvc/orders/1", records[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Leaves_opted_out_actions_unchanged()
    {
        await using var app = await StartAsync();

        var root = await GetJsonAsync(app.Client, "/mvc/orders/plain/42");

        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.False(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Hal_format_emits_links_only_with_hal_content_type()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.Hal);

        var response = await app.Client.GetAsync("/mvc/orders/42");
        var root = await ReadJsonAsync(response);

        Assert.True(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HalForms_format_emits_templates_with_input_fields()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.HalForms);

        var root = await GetJsonAsync(app.Client, "/mvc/orders/42");
        var cancel = root.GetProperty("_templates").GetProperty("cancel");

        Assert.Equal("POST", cancel.GetProperty("method").GetString());
        Assert.EndsWith("/mvc/orders/42/cancel", cancel.GetProperty("target").GetString());

        var reason = cancel.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "reason");
        Assert.True(reason.GetProperty("required").GetBoolean());
    }

    private static async Task<TestApp> StartAsync(Action<CairnOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(MvcOrdersController).Assembly);
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new MvcOrderLinks());
            o.AddPaging<CustomMvcPage<MvcOrder>>(p => new PagedView(p.Records, p.PageNo, p.Size, p.Total));
            configure?.Invoke(o);
        });

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        return new TestApp(app, app.GetTestClient());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
        => await ReadJsonAsync(await client.GetAsync(path));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TestApp(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}

[ApiController]
[Route("mvc/orders")]
public sealed class MvcOrdersController : ControllerBase
{
    [HttpGet("{id:int}", Name = "Mvc_GetOrder")]
    [CairnLinks]
    public MvcOrder Get(int id) => new(id, "Pending");

    [HttpGet("shipped/{id:int}")]
    [CairnLinks]
    public MvcOrder GetShipped(int id) => new(id, "Shipped");

    [HttpGet("find/{id:int}")]
    [CairnLinks]
    public ActionResult<MvcOrder> Find(int id) => id > 0 ? new MvcOrder(id, "Pending") : NotFound();

    [HttpGet]
    [CairnLinks]
    public IEnumerable<MvcOrder> List() => [new(1, "Pending"), new(2, "Shipped")];

    [HttpGet("paged")]
    [CairnLinks]
    public PagedResource<MvcOrder> Paged([FromQuery] int page = 1)
        => new([new(1, "Pending"), new(2, "Shipped")], page, PageSize: 10, TotalCount: 25);

    [HttpGet("cursor")]
    [CairnLinks]
    public CursorPage<MvcOrder> Cursor() => new([new(1, "Pending")], Next: "next-cur", Prev: "prev-cur");

    [HttpGet("custom")]
    [CairnLinks]
    public CustomMvcPage<MvcOrder> Custom()
        => new() { Records = [new(1, "Pending"), new(2, "Shipped")], PageNo = 2, Size = 10, Total = 25 };

    [HttpGet("plain/{id:int}")]
    public MvcOrder Plain(int id) => new(id, "Pending");

    [HttpPost("{id:int}/cancel", Name = "Mvc_Cancel")]
    public IActionResult Cancel(int id) => NoContent();
}

public record MvcOrder(int Id, string Status);

public sealed class MvcCancelRequest
{
    [Required]
    public string Reason { get; init; } = "";
}

public sealed class CustomMvcPage<T>
{
    public required IReadOnlyList<T> Records { get; init; }

    public required int PageNo { get; init; }

    public required int Size { get; init; }

    public required int Total { get; init; }
}

public sealed class MvcOrderLinks : LinkConfig<MvcOrder>
{
    public override void Configure(ILinkBuilder<MvcOrder> builder)
    {
        builder.Self(order => LinkTarget.Route("Mvc_GetOrder", new { id = order.Id }));
        builder.Affordance("cancel", order => LinkTarget.Route("Mvc_Cancel", new { id = order.Id }))
            .Method("POST")
            .Accepts<MvcCancelRequest>()
            .When(order => order.Status == "Pending");
    }
}
