// Cairn.OpenApi targets net10.0 only: Microsoft.AspNetCore.OpenApi's document pipeline does not exist on earlier TFMs.
#if NET10_0_OR_GREATER
using System.Text.Json;
using Cairn.AspNetCore;
using Cairn.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnPaginationBindingOpenApiTests
{
    [Fact]
    public async Task Page_binding_documents_the_page_and_page_size_query_parameters()
    {
        using var document = await GetDocumentAsync();
        var parameters = QueryParameters(document, "/widgets");

        // ApiExplorer can't see into a BindAsync parameter, so without the metadata-driven documentation the
        // operation would advertise no pagination inputs at all.
        var page = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "page");
        Assert.Equal("integer", page.GetProperty("schema").GetProperty("type").GetString());
        Assert.Contains("default 1", page.GetProperty("description").GetString(), StringComparison.Ordinal);

        var size = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "pageSize");
        Assert.Contains("default 20", size.GetProperty("description").GetString(), StringComparison.Ordinal);

        // The handler's PageRequest parameter itself is a binding detail, not a wire input.
        Assert.DoesNotContain(parameters, p => p.GetProperty("name").GetString() == "paging");
    }

    [Fact]
    public async Task Cursor_binding_documents_the_cursor_and_limit_query_parameters()
    {
        using var document = await GetDocumentAsync();
        var parameters = QueryParameters(document, "/feed");

        var cursor = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "cursor");
        Assert.Equal("string", cursor.GetProperty("schema").GetProperty("type").GetString());

        var limit = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "limit");
        Assert.Equal("integer", limit.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Binding_documentation_uses_the_configured_names_and_mentions_the_cap()
    {
        using var document = await GetDocumentAsync(options =>
        {
            options.PageQueryParameter = "p";
            options.PageSizeQueryParameter = "size";
            options.DefaultPageSize = 5;
            options.MaxPageSize = 25;
        });
        var parameters = QueryParameters(document, "/widgets");

        Assert.Single(parameters, p => p.GetProperty("name").GetString() == "p");
        var size = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "size");
        Assert.Contains("default 5", size.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("25", size.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parameter_the_endpoint_documents_itself_is_not_duplicated()
    {
        using var document = await GetDocumentAsync();
        var parameters = QueryParameters(document, "/documented");

        // The handler also declares `int page` — the author's own parameter documentation wins, and the
        // binding adds only what's missing.
        Assert.Single(parameters, p => p.GetProperty("name").GetString() == "page");
        Assert.Single(parameters, p => p.GetProperty("name").GetString() == "pageSize");
    }

    private static async Task<JsonDocument> GetDocumentAsync(Action<CairnOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options => configure?.Invoke(options));
        builder.Services.AddOpenApi(o => o.AddCairnHypermedia());

        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapGet("/widgets", (PageRequest paging) => TypedResults.Ok(paging.ToResource<int>([1], 10))).WithLinks();
        app.MapGet("/feed", (CursorRequest paging) => TypedResults.Ok(new CursorPage<int>([1]))).WithLinks();
        app.MapGet("/documented", (int page, PageRequest paging) => TypedResults.Ok(paging.ToResource<int>([1], 10))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }

    private static List<JsonElement> QueryParameters(JsonDocument document, string path)
    {
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");
        if (!operation.TryGetProperty("parameters", out var parameters))
        {
            return [];
        }

        return [.. parameters.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query")];
    }
}
#endif
