using System.Diagnostics.CodeAnalysis;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Endpoint metadata left by a <see cref="CursorRequest"/> handler parameter, carrying the query parameter
/// names and limit bounds the binding resolved from <see cref="CairnOptions"/> at map time. ApiExplorer
/// cannot see into a <c>BindAsync</c> parameter, so the document generators (Cairn.OpenApi,
/// Cairn.Swashbuckle) read this — by full name and reflection, since they reference only Cairn.Core — to
/// describe the query parameters the endpoint actually binds. The properties are kept under trimming for
/// exactly that reflection.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class CursorBindingMetadata(string cursorParameter, string limitParameter, int defaultLimit, int? maxLimit)
{
    /// <summary>The query parameter the opaque cursor binds from.</summary>
    public string CursorParameter { get; } = cursorParameter;

    /// <summary>The query parameter the limit (page size) binds from.</summary>
    public string LimitParameter { get; } = limitParameter;

    /// <summary>The limit bound when the request doesn't supply one.</summary>
    public int DefaultLimit { get; } = defaultLimit;

    /// <summary>The cap a supplied limit is clamped to, if any.</summary>
    public int? MaxLimit { get; } = maxLimit;
}
