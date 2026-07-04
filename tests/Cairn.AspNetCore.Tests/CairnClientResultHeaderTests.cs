using System.Net;
using System.Text;
using Cairn.Client;

namespace Cairn.AspNetCore.Tests;

// Location and ETag surface on the result types: a create (201) exposes the new resource's URL and validator
// on both the bodiless ClientResult and the typed ClientResult<T>, and a header-less or failed response leaves
// them null (headers are surfaced on success only, mirroring where ReadResultAsync reads them).
public class CairnClientResultHeaderTests
{
    [Fact]
    public async Task A_bodiless_create_surfaces_location_and_etag()
    {
        var http = Client(new ResponseStub(HttpStatusCode.Created, location: "/widgets/44", etag: "\"w44\""));

        var result = await new CairnClient(http).InvokeAsync(new Affordance("create", "/widgets", "POST"));

        Assert.True(result.IsSuccess);
        Assert.Equal(201, result.Status);
        Assert.Equal("/widgets/44", result.Location);
        Assert.Equal("\"w44\"", result.ETag);
    }

    [Fact]
    public async Task A_typed_create_surfaces_location_and_the_resource_etag()
    {
        var http = Client(new ResponseStub(HttpStatusCode.Created, location: "/widgets/43", etag: "\"w43\"", body: """{ "id": 43 }"""));

        var result = await new CairnClient(http).InvokeAsync<Widget>(new Affordance("create", "/widgets", "POST"));

        Assert.True(result.IsSuccess);
        Assert.Equal("/widgets/43", result.Location);
        Assert.Equal("\"w43\"", result.ETag);            // a shortcut for Resource.ETag
        Assert.Equal("\"w43\"", result.Resource!.ETag);
        Assert.Equal(43, result.Value!.Id);
    }

    [Fact]
    public async Task A_relative_location_is_returned_verbatim_for_the_next_get()
    {
        var http = Client(new ResponseStub(HttpStatusCode.Created, location: "/widgets/43?draft=true"));

        var result = await new CairnClient(http).InvokeAsync(new Affordance("create", "/widgets", "POST"));

        // Returned exactly as sent (relative, query intact) so it feeds straight back into GetAsync.
        Assert.Equal("/widgets/43?draft=true", result.Location);
    }

    [Fact]
    public async Task A_failed_typed_result_has_no_location_and_a_null_etag_shortcut()
    {
        var http = Client(new ResponseStub(HttpStatusCode.InternalServerError, location: "/widgets/43", etag: "\"w43\""));

        var result = await new CairnClient(http).InvokeAsync<Widget>(new Affordance("create", "/widgets", "POST"));

        // No Resource on a failure, so the ETag shortcut (Resource?.ETag) collapses to null, and Location — read
        // only on the success path — stays null too.
        Assert.False(result.IsSuccess);
        Assert.Null(result.Resource);
        Assert.Null(result.Location);
        Assert.Null(result.ETag);
    }

    [Fact]
    public async Task A_response_without_the_headers_leaves_location_and_etag_null()
    {
        var http = Client(new ResponseStub(HttpStatusCode.NoContent));

        var result = await new CairnClient(http).InvokeAsync(new Affordance("act", "/act", "POST"));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Location);
        Assert.Null(result.ETag);
    }

    [Fact]
    public async Task A_failed_create_carries_the_problem_not_a_location()
    {
        var http = Client(new ResponseStub(HttpStatusCode.Conflict, location: "/widgets/43", etag: "\"w43\""));

        var result = await new CairnClient(http).InvokeAsync(new Affordance("create", "/widgets", "POST"));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Problem);
        Assert.Null(result.Location);
        Assert.Null(result.ETag);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost") };

    private sealed record Widget(int Id);

    private sealed class ResponseStub(HttpStatusCode status, string? location = null, string? etag = null, string? body = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            if (location is not null)
            {
                response.Headers.TryAddWithoutValidation("Location", location);
            }

            if (etag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return Task.FromResult(response);
        }
    }
}
