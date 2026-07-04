using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Swashbuckle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.AspNetCore.Tests;

// Document-generation edges: envelope interfaces as declared response types, responses without a body type,
// operations another filter has already mutated, MVC default responses, and a host that registers
// ILinkConfigProvider without the rest of AddCairn.
public class CairnSwashbuckleEdgeTests
{
    [Fact]
    public async Task An_envelope_interface_response_type_documents_the_negotiable_media_types()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/edge/paged-interface").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        // The declared response type is IPagedResource itself; the wire still decorates the envelope, so
        // the document advertises the negotiable media types just like the concrete PagedResource<T>.
        Assert.True(content.TryGetProperty("application/json", out _));
        Assert.True(content.TryGetProperty("application/hal+json", out _));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out _));
    }

    [Fact]
    public async Task A_cursor_envelope_interface_response_type_documents_the_negotiable_media_types()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/edge/cursor-interface").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("application/json", out _));
        Assert.True(content.TryGetProperty("application/hal+json", out _));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out _));
    }

    [Fact]
    public async Task A_bodyless_response_keeps_no_media_types()
    {
        using var document = await GetDocumentAsync();
        var response = document.RootElement
            .GetProperty("paths").GetProperty("/edge/void").GetProperty("post")
            .GetProperty("responses").GetProperty("204");

        // A 204 has no body type (void) — nothing to decorate, nothing to negotiate. The synthetic
        // response type another filter added without any Type at all is skipped the same way.
        Assert.False(response.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task An_operation_whose_responses_another_filter_removed_is_left_alone()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/edge/hidden").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        // An earlier filter nulled the operation's responses; Cairn's filter must no-op instead of crash,
        // so the response a later filter rebuilt has no negotiated media types despite the linked type.
        Assert.True(content.TryGetProperty("application/json", out _));
        Assert.False(content.TryGetProperty("application/hal+json", out _));
    }

    [Fact]
    public async Task An_mvc_default_response_with_a_linked_type_documents_the_negotiable_media_types()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/swedge/orders/{id}").GetProperty("get")
            .GetProperty("responses").GetProperty("default").GetProperty("content");

        // [ProducesDefaultResponseType] maps to the "default" responses key rather than a status code.
        Assert.True(content.TryGetProperty("application/json", out _));
        Assert.True(content.TryGetProperty("application/hal+json", out _));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out _));
    }

    [Fact]
    public async Task A_bare_link_config_provider_without_AddCairn_still_documents_hypermedia()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Only the Core-level provider, not AddCairn: no IPaginationEnvelopeProvider is registered, so
        // adapted envelopes are unknown — but configured types and the built-in envelopes still document.
        builder.Services.AddSingleton<ILinkConfigProvider>(new LinkConfigRegistry().Add(new EdgeOrderLinks()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c => c.AddCairnHypermedia());

        await using var app = builder.Build();
        app.UseSwagger();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new SwaggerEdgeOrder(id))).WithLinks();
        app.MapGet("/plain", () => TypedResults.Ok(new EdgePlainNote("hi")));

        await app.StartAsync();
        using var client = app.GetTestClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        var linked = document.RootElement
            .GetProperty("paths").GetProperty("/orders/{id}").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");
        Assert.True(linked.TryGetProperty("application/hal+json", out _));

        var plain = document.RootElement
            .GetProperty("paths").GetProperty("/plain").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");
        Assert.False(plain.TryGetProperty("application/hal+json", out _));
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(SwaggerEdgeOrdersController).Assembly);
        builder.Services.AddCairn(o => o.AddLinks(new EdgeOrderLinks()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            // Deliberately hostile filter ordering: one filter hides an operation's responses and another
            // fakes a response type with no Type before Cairn's filter runs; a final filter rebuilds the
            // hidden responses so the document still serializes.
            c.OperationFilter<MutatingPreFilter>();
            c.AddCairnHypermedia();
            c.OperationFilter<RestoreResponsesFilter>();
        });

        await using var app = builder.Build();
        app.UseSwagger();
        app.MapControllers();
        app.MapGet("/edge/paged-interface", IPagedResource () => new PagedResource<SwaggerEdgeOrder>([new(1)], 1, 10, 25)).WithLinks();
        app.MapGet("/edge/cursor-interface", ICursorPagedResource () => new CursorPage<SwaggerEdgeOrder>([new(1)], Next: "n")).WithLinks();
        app.MapPost("/edge/void", () => TypedResults.NoContent());
        app.MapGet("/edge/hidden", () => TypedResults.Ok(new SwaggerEdgeOrder(1)));

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
    }

    private sealed record EdgePlainNote(string Text);

    private sealed class EdgeOrderLinks : LinkConfig<SwaggerEdgeOrder>
    {
        public override void Configure(ILinkBuilder<SwaggerEdgeOrder> builder)
            => builder.Self(order => LinkTarget.Uri($"/swedge/orders/{order.Id}"));
    }

    private sealed class MutatingPreFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (context.ApiDescription.RelativePath == "edge/hidden")
            {
                operation.Responses = null;
            }

            if (context.ApiDescription.RelativePath == "edge/void")
            {
                // A response type with no Type at all — the nullable slot ApiExplorer leaves open.
                context.ApiDescription.SupportedResponseTypes.Add(new ApiResponseType { StatusCode = 299 });
            }
        }
    }

    private sealed class RestoreResponsesFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
            => operation.Responses ??= new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "OK",
                    Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() },
                },
            };
    }
}

/// <summary>An order DTO shared by the Swashbuckle edge tests and their MVC controller.</summary>
public sealed record SwaggerEdgeOrder(int Id);

/// <summary>Documents its default response as the linked order type.</summary>
[ApiController]
[Route("swedge/orders")]
[CairnLinks]
public sealed class SwaggerEdgeOrdersController : ControllerBase
{
    /// <summary>Returns the order.</summary>
    [HttpGet("{id:int}", Name = "SwEdge_GetOrder")]
    [ProducesDefaultResponseType(typeof(SwaggerEdgeOrder))]
    public ActionResult<SwaggerEdgeOrder> Get(int id) => new SwaggerEdgeOrder(id);
}
