using System.Net;
using Cairn.AspNetCore;
using Cairn.AspNetCore.Explorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Explorer.Tests;

public class CairnExplorerMiddlewareTests
{
    private static async Task<WebApplication> StartAsync(
        string environment = "Development",
        Action<CairnExplorerOptions>? configure = null,
        Action<CairnOptions>? cairn = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        if (cairn is not null)
        {
            builder.Services.AddCairn(cairn);
        }

        var app = builder.Build();
        app.UseCairnExplorer(configure);
        app.MapGet("/api/ping", () => "pong");
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Serves_the_explorer_html_at_the_default_path_in_development()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/explorer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cairn HAL Explorer", body);
        Assert.Contains("id=\"cairn-config\"", body);
        // The bootstrap config is injected in place of the placeholder.
        Assert.DoesNotContain("__CAIRN_EXPLORER_CONFIG__", body);
        Assert.Contains("\"entryPoint\":\"/\"", body);
    }

    [Fact]
    public async Task Serves_the_explorer_on_the_trailing_slash_form()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/explorer/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Passes_through_paths_below_the_mount_point()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // No asset endpoint below the mount point, so it is not the explorer and falls through to a 404.
        var response = await client.GetAsync("/explorer/assets/app.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Passes_through_unrelated_paths()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/api/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pong", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Does_not_intercept_non_get_requests_to_the_mount_point()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("/explorer", content: null);

        // The explorer only answers GET; a POST is not the UI, so it falls through (404 here).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Is_not_served_outside_development_by_default()
    {
        await using var app = await StartAsync(environment: Environments.Production);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/explorer");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_true_serves_it_outside_development()
    {
        await using var app = await StartAsync(
            environment: Environments.Production,
            configure: options => options.Enabled = true);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/explorer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Enabled_false_disables_it_in_development()
    {
        await using var app = await StartAsync(configure: options => options.Enabled = false);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/explorer");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Honors_a_custom_path_entry_point_and_title()
    {
        await using var app = await StartAsync(configure: options =>
        {
            options.Path = "/hal";
            options.EntryPoint = "/api";
            options.Title = "Store API Explorer";
        });
        using var client = app.GetTestClient();

        var served = await client.GetAsync("/hal");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var body = await served.Content.ReadAsStringAsync();
        Assert.Contains("\"entryPoint\":\"/api\"", body);
        Assert.Contains("Store API Explorer", body);

        // The default path is no longer served.
        var defaultPath = await client.GetAsync("/explorer");
        Assert.Equal(HttpStatusCode.NotFound, defaultPath.StatusCode);
    }

    [Fact]
    public async Task Reflects_the_apps_configured_media_types()
    {
        await using var app = await StartAsync(cairn: options =>
            options.MediaTypes.Hal = "application/vnd.acme.hal+json");
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/explorer");

        // The custom HAL media type reaches the UI's Accept selector (the '+' is JSON-escaped in the payload).
        Assert.Contains("vnd.acme.hal", body);
    }

    [Fact]
    public void UseCairnExplorer_rejects_a_null_builder()
        => Assert.Throws<ArgumentNullException>(() => ((IApplicationBuilder)null!).UseCairnExplorer());
}
