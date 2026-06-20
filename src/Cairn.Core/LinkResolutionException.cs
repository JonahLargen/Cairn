namespace Cairn;

/// <summary>Thrown in strict mode when a link target cannot be resolved to a URL.</summary>
public sealed class LinkResolutionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LinkResolutionException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public LinkResolutionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public LinkResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
