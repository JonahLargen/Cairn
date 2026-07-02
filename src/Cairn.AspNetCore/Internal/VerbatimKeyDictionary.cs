using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// A string-keyed dictionary whose keys are serialized verbatim. Hypermedia keys — link relations,
/// affordance/template names, embedded rels, problem members — are protocol identifiers, so the host's
/// <see cref="JsonSerializerOptions.DictionaryKeyPolicy"/> (camelCase, snake_case, ...) must never rename
/// them; a plain <see cref="Dictionary{TKey, TValue}"/> would have its keys rewritten by that policy.
/// </summary>
[JsonConverter(typeof(VerbatimKeyDictionaryConverterFactory))]
internal sealed class VerbatimKeyDictionary<TValue> : Dictionary<string, TValue>
{
    public VerbatimKeyDictionary(IEqualityComparer<string>? comparer = null)
        : base(comparer ?? StringComparer.Ordinal)
    {
    }
}

/// <summary>Creates the write-only converter that emits <see cref="VerbatimKeyDictionary{TValue}"/> keys verbatim.</summary>
internal sealed class VerbatimKeyDictionaryConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(VerbatimKeyDictionary<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(VerbatimKeyDictionaryConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class VerbatimKeyDictionaryConverter<TValue> : JsonConverter<VerbatimKeyDictionary<TValue>>
    {
        public override VerbatimKeyDictionary<TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, VerbatimKeyDictionary<TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (key, item) in value)
            {
                // WritePropertyName escapes but never renames — DictionaryKeyPolicy is deliberately bypassed.
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, item, options);
            }

            writer.WriteEndObject();
        }
    }
}
