using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cairn.Testing;

/// <summary>Options controlling <see cref="HypermediaSnapshot"/> rendering.</summary>
public sealed class HypermediaSnapshotOptions
{
    /// <summary>Keeps only the hypermedia parts (<c>_links</c>, <c>_actions</c>, <c>_templates</c>, <c>_embedded</c>) of each resource, dropping its data properties. Defaults to <see langword="false"/> (the whole payload is rendered).</summary>
    public bool HypermediaOnly { get; init; }

    /// <summary>Normalizes volatile URI values: applied to every <c>href</c> and <c>target</c> string before it is written (e.g. to strip a random test-server port or replace the value with a placeholder).</summary>
    public Func<string, string>? NormalizeHref { get; init; }
}

/// <summary>
/// Renders a hypermedia payload as stable, indented JSON for snapshot testing with any snapshot tool:
/// object keys are sorted ordinally, newlines are normalized to <c>\n</c>, and volatile href values can be
/// normalized via <see cref="HypermediaSnapshotOptions.NormalizeHref"/>.
/// </summary>
public static class HypermediaSnapshot
{
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal) { "_links", "_actions", "_templates", "_embedded" };

    /// <summary>Renders a JSON payload as a normalized snapshot string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static string Render(string json, HypermediaSnapshotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        options ??= new HypermediaSnapshotOptions();

        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        // Relaxed escaping keeps hrefs and placeholders (e.g. "<host>") readable in the snapshot file.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteResource(writer, document.RootElement, options);
        }

        // Utf8JsonWriter's newline is environment-dependent on some targets; pin it for stable snapshots.
        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n");
    }

    /// <summary>Reads an <see cref="HttpResponseMessage"/> body and renders it as a normalized snapshot string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public static async Task<string> RenderAsync(HttpResponseMessage response, HypermediaSnapshotOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Render(json, options);
    }

    // A "resource" object is subject to HypermediaOnly filtering; its _embedded values are resources again.
    private static void WriteResource(Utf8JsonWriter writer, JsonElement element, HypermediaSnapshotOptions options)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            WriteValue(writer, null, element, options);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in SortedProperties(element))
        {
            if (options.HypermediaOnly && !ReservedKeys.Contains(property.Name))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            if (property.NameEquals("_embedded") && property.Value.ValueKind == JsonValueKind.Object)
            {
                WriteEmbedded(writer, property.Value, options);
            }
            else
            {
                WriteValue(writer, property.Name, property.Value, options);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteEmbedded(Utf8JsonWriter writer, JsonElement embedded, HypermediaSnapshotOptions options)
    {
        writer.WriteStartObject();
        foreach (var relation in SortedProperties(embedded))
        {
            writer.WritePropertyName(relation.Name);
            if (relation.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in relation.Value.EnumerateArray())
                {
                    WriteResource(writer, item, options);
                }

                writer.WriteEndArray();
            }
            else
            {
                WriteResource(writer, relation.Value, options);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, string? propertyName, JsonElement element, HypermediaSnapshotOptions options)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in SortedProperties(element))
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Name, property.Value, options);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteValue(writer, null, item, options);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var value = element.GetString()!;
                if (options.NormalizeHref is { } normalize && propertyName is "href" or "target")
                {
                    value = normalize(value);
                }

                writer.WriteStringValue(value);
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static IOrderedEnumerable<JsonProperty> SortedProperties(JsonElement element)
        => element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal);
}
