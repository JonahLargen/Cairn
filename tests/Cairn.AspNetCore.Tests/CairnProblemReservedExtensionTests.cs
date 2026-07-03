using Cairn.AspNetCore;

namespace Cairn.AspNetCore.Tests;

public class CairnProblemReservedExtensionTests
{
    [Theory]
    [InlineData("type")]
    [InlineData("title")]
    [InlineData("status")]
    [InlineData("detail")]
    [InlineData("instance")]
    [InlineData("_links")]
    [InlineData("_actions")]
    public void WithExtension_rejects_reserved_member_names(string name)
    {
        var problem = CairnResults.Problem(409, title: "Conflict");

        // WithExtension("status", ...) used to silently clobber the numeric status member on the wire.
        var failure = Assert.Throws<ArgumentException>(() => problem.WithExtension(name, "boom"));
        Assert.Contains("reserved", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithExtension_accepts_ordinary_extension_members()
    {
        var problem = CairnResults.Problem(409).WithExtension("traceId", "abc").WithExtension("Status", 1);

        // "Status" (different case) is a distinct JSON member, not the RFC 9457 "status" — allowed.
        Assert.NotNull(problem);
    }
}
