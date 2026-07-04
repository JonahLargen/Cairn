using System.Text;
using Cairn.Testing;

namespace Cairn.AspNetCore.Tests;

// HypermediaSnapshot corners: rendering straight from an HttpResponseMessage, non-object payloads,
// and single-object embedded entries. Plus the assertion exception's construction contract.
public class CairnSnapshotEdgeTests
{
    [Fact]
    public async Task RenderAsync_reads_the_response_body()
    {
        using var response = new HttpResponseMessage
        {
            Content = new StringContent("""{"b":2,"a":1}""", Encoding.UTF8, "application/json"),
        };

        var snapshot = await HypermediaSnapshot.RenderAsync(response);

        // Same normalization as Render: keys sorted ordinally, \n newlines.
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2\n}", snapshot);
    }

    [Fact]
    public async Task RenderAsync_requires_a_response()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => HypermediaSnapshot.RenderAsync(null!));
    }

    [Fact]
    public void An_array_payload_renders_as_an_array()
    {
        Assert.Equal("[\n  2,\n  1\n]", HypermediaSnapshot.Render("[2,1]"));
    }

    [Fact]
    public void A_single_object_embedded_entry_renders_as_a_resource()
    {
        var json = """{"_embedded":{"customer":{"b":2,"a":1}}}""";

        var snapshot = HypermediaSnapshot.Render(json);

        // The embedded resource is normalized (keys sorted) like a top-level one, not copied verbatim.
        Assert.Contains("\"customer\": {\n      \"a\": 1,\n      \"b\": 2\n    }", snapshot);
    }

    [Fact]
    public void The_assertion_exception_supports_the_standard_constructors()
    {
        var bare = new CairnAssertionException();
        var inner = new InvalidOperationException("cause");
        var wrapped = new CairnAssertionException("expected a link", inner);

        Assert.NotNull(bare.Message);
        Assert.Equal("expected a link", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }
}
