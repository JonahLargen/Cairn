namespace Cairn.Client;

/// <summary>Thrown by <c>EnsureSuccess()</c> when a request returned an HTTP error status.</summary>
public sealed class CairnClientException : Exception
{
    /// <summary>Creates the exception for an error status and its parsed problem detail.</summary>
    public CairnClientException(int status, Problem? problem)
        : base(problem?.Title is { } title ? $"The request failed with status {status}: {title}" : $"The request failed with status {status}.")
    {
        Status = status;
        Problem = problem;
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The parsed problem detail, if any.</summary>
    public Problem? Problem { get; }
}
