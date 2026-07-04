namespace Cairn.Core.Tests;

public class LinkResolutionExceptionTests
{
    [Fact]
    public void Supports_the_standard_exception_constructors()
    {
        var bare = new LinkResolutionException();
        var inner = new InvalidOperationException("cause");
        var wrapped = new LinkResolutionException("could not resolve 'self'", inner);

        Assert.NotNull(bare.Message);
        Assert.Equal("could not resolve 'self'", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }
}
