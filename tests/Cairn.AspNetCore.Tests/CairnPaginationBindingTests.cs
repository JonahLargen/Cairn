using System.Net;
using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnPaginationBindingTests
{
    [Fact]
    public async Task PageRequest_binds_the_documented_defaults_and_flows_into_the_envelope()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        var body = JsonDocument.Parse(await client.GetStringAsync("/widgets")).RootElement;

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(100, body.GetProperty("totalCount").GetInt32());

        // The envelope carries the bound values, so the derived navigation links describe the same page.
        var links = body.GetProperty("_links");
        Assert.Contains("page=1", links.GetProperty("self").GetProperty("href").GetString(), StringComparison.Ordinal);
        Assert.Contains("page=2", links.GetProperty("next").GetProperty("href").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageRequest_binds_the_supplied_page_and_size_and_computes_skip()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        var body = JsonDocument.Parse(await client.GetStringAsync("/widgets?page=3&pageSize=10")).RootElement;

        Assert.Equal(3, body.GetProperty("page").GetInt32());
        Assert.Equal(10, body.GetProperty("pageSize").GetInt32());

        // The handler puts paging.Skip on the page, so the wire proves the offset math: (3 - 1) * 10.
        Assert.Equal(20, body.GetProperty("items")[0].GetInt32());
    }

    [Fact]
    public async Task PageRequest_binds_the_configured_parameter_names_and_bounds()
    {
        await using var app = await BuildAppAsync(options =>
        {
            options.PageQueryParameter = "p";
            options.PageSizeQueryParameter = "size";
            options.DefaultPageSize = 5;
            options.MaxPageSize = 25;
        });
        using var client = app.GetTestClient();

        // An omitted size binds the configured default.
        var defaulted = JsonDocument.Parse(await client.GetStringAsync("/widgets")).RootElement;
        Assert.Equal(5, defaulted.GetProperty("pageSize").GetInt32());

        // A size above the cap is clamped — and the envelope echoes the clamped value, so the metadata
        // describes the page actually served.
        var body = JsonDocument.Parse(await client.GetStringAsync("/widgets?p=2&size=100")).RootElement;
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(25, body.GetProperty("pageSize").GetInt32());

        // The binding reads the same parameter the pagination links swap, so navigation stays consistent.
        var next = body.GetProperty("_links").GetProperty("next").GetProperty("href").GetString()!;
        Assert.Contains("p=3", next, StringComparison.Ordinal);
        Assert.Contains("size=100", next, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/widgets?page=0")]
    [InlineData("/widgets?page=-1")]
    [InlineData("/widgets?page=abc")]
    [InlineData("/widgets?page=")]
    [InlineData("/widgets?page=1&page=2")]
    [InlineData("/widgets?pageSize=0")]
    [InlineData("/widgets?pageSize=nope")]
    [InlineData("/feed?limit=0")]
    [InlineData("/feed?limit=x")]
    [InlineData("/feed?cursor=a&cursor=b")]
    public async Task A_repeated_or_malformed_pagination_parameter_fails_with_400(string url)
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CursorRequest_binds_the_cursor_and_limit()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        // Absent (or empty) cursor means the first page; absent limit means the default page size.
        var first = JsonDocument.Parse(await client.GetStringAsync("/feed")).RootElement;
        Assert.Equal(JsonValueKind.Null, first.GetProperty("cursor").ValueKind);
        Assert.Equal(20, first.GetProperty("limit").GetInt32());

        var empty = JsonDocument.Parse(await client.GetStringAsync("/feed?cursor=")).RootElement;
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("cursor").ValueKind);

        var body = JsonDocument.Parse(await client.GetStringAsync("/feed?cursor=abc&limit=5")).RootElement;
        Assert.Equal("abc", body.GetProperty("cursor").GetString());
        Assert.Equal(5, body.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task CursorRequest_binds_the_configured_parameter_names_and_cap()
    {
        await using var app = await BuildAppAsync(options =>
        {
            options.CursorQueryParameter = "after";
            options.LimitQueryParameter = "take";
            options.MaxPageSize = 10;
        });
        using var client = app.GetTestClient();

        var body = JsonDocument.Parse(await client.GetStringAsync("/feed?after=xyz&take=50")).RootElement;

        Assert.Equal("xyz", body.GetProperty("cursor").GetString());
        Assert.Equal(10, body.GetProperty("limit").GetInt32());

        // With only the cap configured, the unconfigured default page size clamps to it.
        var defaulted = JsonDocument.Parse(await client.GetStringAsync("/feed")).RootElement;
        Assert.Equal(10, defaulted.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Binding_works_with_the_documented_defaults_when_Cairn_is_not_registered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        await using var app = builder.Build();
        app.MapGet("/plain", (PageRequest paging) => new { paging.Page, paging.PageSize });

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = JsonDocument.Parse(await client.GetStringAsync("/plain?page=2")).RootElement;

        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public void Bind_reads_the_current_request_for_use_outside_minimal_api_binding()
    {
        // A controller action (or filter/middleware) calls Bind(HttpContext) directly — same rules, same
        // configured names.
        var services = new ServiceCollection();
        services.AddCairn(options =>
        {
            options.PageQueryParameter = "p";
            options.MaxPageSize = 30;
        });
        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.QueryString = new QueryString("?p=4&pageSize=99&cursor=c1&limit=3");

        var page = PageRequest.Bind(context);
        Assert.Equal(4, page.Page);
        Assert.Equal(30, page.PageSize);
        Assert.Equal(90, page.Skip);

        var cursor = CursorRequest.Bind(context);
        Assert.Equal("c1", cursor.Cursor);
        Assert.Equal(3, cursor.Limit);

        var resource = page.ToResource([1, 2], totalCount: 200);
        Assert.Equal(4, resource.Page);
        Assert.Equal(30, resource.PageSize);
        Assert.Equal(200, resource.TotalCount);
    }

    [Fact]
    public void Bind_guards_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => PageRequest.Bind(null!));
        Assert.Throws<ArgumentNullException>(() => CursorRequest.Bind(null!));

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        Assert.Throws<ArgumentNullException>(() => PageRequest.Bind(context).ToResource<int>(null!, 0));
    }

    [Fact]
    public void Page_size_options_reject_non_positive_values()
    {
        var options = new CairnOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.DefaultPageSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxPageSize = 0);

        // Un-setting the cap is fine.
        options.MaxPageSize = 10;
        options.MaxPageSize = null;
        Assert.Null(options.MaxPageSize);
    }

    [Fact]
    public void A_default_page_size_above_the_cap_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddCairn(options =>
        {
            options.DefaultPageSize = 50;
            options.MaxPageSize = 10;
        });
        using var provider = services.BuildServiceProvider();

        // The contradiction surfaces when the options freeze (first resolution at host start), not per request.
        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<CairnOptions>());
        Assert.Contains("MaxPageSize", failure.Message, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> BuildAppAsync(Action<CairnOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options => configure?.Invoke(options));

        var app = builder.Build();

        // The offset endpoint surfaces Skip as the page's only item so the offset math is observable on the wire.
        app.MapGet("/widgets", (PageRequest paging) => paging.ToResource<int>([paging.Skip], totalCount: 100)).WithLinks();
        app.MapGet("/feed", (CursorRequest paging) => new { paging.Cursor, paging.Limit });

        await app.StartAsync();
        return app;
    }
}
