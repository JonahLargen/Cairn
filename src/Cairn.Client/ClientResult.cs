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

    /// <summary>
    /// Whether the request succeeded — a 2xx status, or <c>304 Not Modified</c> for a conditional request
    /// (see <see cref="IsNotModified"/>). When <see langword="true"/>, <see cref="Resource"/> is non-null;
    /// otherwise <see cref="Problem"/> is non-null.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Resource))]
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool IsSuccess { get; }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>
    /// Whether the server returned <c>304 Not Modified</c> (in response to a conditional GET). The result is
    /// successful but carries no body — keep using the cached representation the ETag was taken from.
    /// </summary>
    public bool IsNotModified => Status == 304;

    /// <summary>The resource and its hypermedia, when <see cref="IsSuccess"/>. Empty (no value) on a <c>304</c>.</summary>
    public Resource<T>? Resource { get; }

    /// <summary>The resource's deserialized value on success, otherwise <see langword="default"/>. A shortcut for <c>Resource?.Value</c>.</summary>
    public T? Value => Resource is { } resource ? resource.Value : default;

    /// <summary>The parsed problem detail, when not <see cref="IsSuccess"/>.</summary>
    public Problem? Problem { get; }

    /// <summary>Returns the <see cref="Resource"/> on success, otherwise throws. On a <c>304</c> the resource is empty — no body was sent.</summary>
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

    /// <summary>
    /// Whether the request succeeded — a 2xx status, or <c>304 Not Modified</c> for a conditional request
    /// (see <see cref="IsNotModified"/>). When <see langword="false"/>, <see cref="Problem"/> is non-null.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool IsSuccess { get; }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>Whether the server returned <c>304 Not Modified</c> (in response to a conditional request).</summary>
    public bool IsNotModified => Status == 304;

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
