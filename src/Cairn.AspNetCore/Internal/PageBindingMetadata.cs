using System.Diagnostics.CodeAnalysis;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Endpoint metadata left by a <see cref="PageRequest"/> handler parameter, carrying the query parameter
/// names and page-size bounds the binding resolved from <see cref="CairnOptions"/> at map time. ApiExplorer
/// cannot see into a <c>BindAsync</c> parameter, so the document generators (Cairn.OpenApi,
/// Cairn.Swashbuckle) read this — by full name and reflection, since they reference only Cairn.Core — to
/// describe the query parameters the endpoint actually binds. The properties are kept under trimming for
/// exactly that reflection.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class PageBindingMetadata(string pageParameter, string pageSizeParameter, int defaultPageSize, int? maxPageSize)
{
    /// <summary>The query parameter the 1-based page number binds from.</summary>
    public string PageParameter { get; } = pageParameter;

    /// <summary>The query parameter the page size binds from.</summary>
    public string PageSizeParameter { get; } = pageSizeParameter;

    /// <summary>The page size bound when the request doesn't supply one.</summary>
    public int DefaultPageSize { get; } = defaultPageSize;

    /// <summary>The cap a supplied page size is clamped to, if any.</summary>
    public int? MaxPageSize { get; } = maxPageSize;
}
