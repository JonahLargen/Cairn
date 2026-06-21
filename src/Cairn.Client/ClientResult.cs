using System.Diagnostics.CodeAnalysis;

namespace Cairn.Client;

/// <summary>The outcome of a request that returns a resource: a <see cref="Resource{T}"/> on success, or a <see cref="Client.Problem"/> on an HTTP error status.</summary>
/// <typeparam name="T">The resource body type.</typeparam>
public sealed class ClientResult<T>
{
    private ClientResult(bool isSuccess, int status, Resource<T>? resource, Problem? problem)
    {
        IsSuccess = isSuccess;
        Status = status;
        Resource = resource;
        Problem = problem;
    }

    /// <summary>Whether the response had a success (2xx) status. When <see langword="true"/>, <see cref="Resource"/> is non-null; otherwise <see cref="Problem"/> is non-null.</summary>
    [MemberNotNullWhen(true, nameof(Resource))]
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool IsSuccess { get; }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The resource and its hypermedia, when <see cref="IsSuccess"/>.</summary>
    public Resource<T>? Resource { get; }

    /// <summary>The parsed problem detail, when not <see cref="IsSuccess"/>.</summary>
    public Problem? Problem { get; }

    /// <summary>Returns the <see cref="Resource"/> on success, otherwise throws.</summary>
    /// <exception cref="CairnClientException">The response was an HTTP error status.</exception>
    public Resource<T> EnsureSuccess()
        => IsSuccess ? Resource! : throw new CairnClientException(Status, Problem);

    internal static ClientResult<T> Success(int status, Resource<T> resource) => new(true, status, resource, null);

    internal static ClientResult<T> Failure(int status, Problem problem) => new(false, status, null, problem);
}

/// <summary>The outcome of a request that returns no resource (e.g. an invoked action): success, or a <see cref="Client.Problem"/> on an HTTP error status.</summary>
public sealed class ClientResult
{
    private ClientResult(bool isSuccess, int status, Problem? problem)
    {
        IsSuccess = isSuccess;
        Status = status;
        Problem = problem;
    }

    /// <summary>Whether the response had a success (2xx) status. When <see langword="false"/>, <see cref="Problem"/> is non-null.</summary>
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool IsSuccess { get; }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The parsed problem detail, when not <see cref="IsSuccess"/>.</summary>
    public Problem? Problem { get; }

    /// <summary>Throws if the response was an HTTP error status.</summary>
    /// <exception cref="CairnClientException">The response was an HTTP error status.</exception>
    public void EnsureSuccess()
    {
        if (!IsSuccess)
        {
            throw new CairnClientException(Status, Problem);
        }
    }

    internal static ClientResult Success(int status) => new(true, status, null);

    internal static ClientResult Failure(int status, Problem problem) => new(false, status, problem);
}
