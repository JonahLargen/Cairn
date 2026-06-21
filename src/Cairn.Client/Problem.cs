using System.Text.Json;

namespace Cairn.Client;

/// <summary>An RFC 9457 problem detail parsed from an error response (<c>application/problem+json</c>).</summary>
public sealed record Problem
{
    /// <summary>A URI reference identifying the problem type.</summary>
    public string? Type { get; init; }

    /// <summary>A short, human-readable summary of the problem.</summary>
    public string? Title { get; init; }

    /// <summary>The HTTP status code.</summary>
    public int? Status { get; init; }

    /// <summary>A human-readable explanation specific to this occurrence.</summary>
    public string? Detail { get; init; }

    /// <summary>A URI reference identifying the specific occurrence.</summary>
    public string? Instance { get; init; }

    /// <summary>Any non-standard members (extensions), such as validation <c>errors</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>Reads a <see cref="Problem"/> from an error response body, falling back to the status when it isn't problem+json.</summary>
internal static class ProblemReader
{
    public static Problem ReadFrom(string? body, int status, string? reasonPhrase)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return FromObject(document.RootElement, status, reasonPhrase);
                }
            }
            catch (JsonException)
            {
                // Not JSON — fall back to a status-only problem.
            }
        }

        return new Problem { Title = reasonPhrase, Status = status };
    }

    private static Problem FromObject(JsonElement root, int status, string? reasonPhrase)
    {
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
        {
            if (!IsStandard(member.Name))
            {
                extensions[member.Name] = member.Value.Clone();
            }
        }

        return new Problem
        {
            Type = GetString(root, "type"),
            Title = GetString(root, "title") ?? reasonPhrase,
            Status = GetInt(root, "status") ?? status,
            Detail = GetString(root, "detail"),
            Instance = GetString(root, "instance"),
            Extensions = extensions,
        };
    }

    private static bool IsStandard(string name)
        => name is "type" or "title" or "status" or "detail" or "instance";

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
