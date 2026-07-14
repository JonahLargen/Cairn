namespace Cairn.Mcp.Internal;

/// <summary>Builds MCP tool names from resource names and affordance relations.</summary>
internal static class CairnMcpToolName
{
    /// <summary>The reserved suffix of the per-resource state-inspection tool.</summary>
    public const string GetSuffix = "get";

    /// <summary>
    /// The tool name for an affordance: <c>{resource}_{relation}</c>, with any character MCP tool names
    /// disallow replaced by <c>-</c> (a relation may be a URI; tool names are limited to letters, digits,
    /// <c>_</c> and <c>-</c>).
    /// </summary>
    public static string For(string resourceName, string relation)
    {
        var name = $"{resourceName}_{relation}";
        char[]? sanitized = null;
        for (var i = 0; i < name.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(name[i]) && name[i] is not '_' and not '-')
            {
                (sanitized ??= name.ToCharArray())[i] = '-';
            }
        }

        return sanitized is null ? name : new string(sanitized);
    }

    /// <summary>The description of the <c>id</c> argument identifying which resource instance a tool call targets.</summary>
    public static string IdDescription(string resourceName)
        => $"The identifier of the {resourceName} the action targets, as it appears in the resource's URL.";
}
