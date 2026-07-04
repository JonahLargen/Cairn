using System.Linq;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

public class HypermediaProblemMetadataTests
{
    [Fact]
    public async Task PopulateMetadata_contributes_a_problem_json_500_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        // A declared HypermediaProblem return type triggers IEndpointMetadataProvider.PopulateMetadata; so does
        // its use as one arm of a Results<...> union alongside a success result.
        app.MapGet("/direct", HypermediaProblem () => new HypermediaProblem(404));
        app.MapGet("/union", Results<Ok<int>, HypermediaProblem> () => TypedResults.Ok(1));

        await app.StartAsync();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var route in new[] { "/direct", "/union" })
        {
            var endpoint = endpoints.OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == route);
            var problem = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Single(m => m.StatusCode == 500);

            Assert.Contains("application/problem+json", problem.ContentTypes);
            Assert.Equal(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), problem.Type);
        }
    }
}
