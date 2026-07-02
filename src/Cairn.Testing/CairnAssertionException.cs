namespace Cairn.Testing;

/// <summary>The exception thrown when a Cairn hypermedia assertion fails.</summary>
/// <remarks>
/// Deliberately framework-agnostic: every test framework surfaces a plain exception's message as the
/// test failure, so Cairn.Testing needs no dependency on a third-party assertion library.
/// </remarks>
public sealed class CairnAssertionException : Exception
{
    /// <summary>Creates the exception without a message.</summary>
    public CairnAssertionException()
    {
    }

    /// <summary>Creates the exception with a message describing the failed expectation.</summary>
    public CairnAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message describing the failed expectation and the underlying cause.</summary>
    public CairnAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
