using System.Text.Json.Serialization;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Source-generated JSON metadata for Cairn's emission types (<c>_links</c>/<c>_actions</c>/<c>_templates</c>
/// payloads), combined into the host's resolver chain so hypermedia serializes even under a source-gen-only
/// <see cref="System.Text.Json.JsonSerializerOptions.TypeInfoResolver"/> (the standard Native AOT setup),
/// where reflection-based contracts are unavailable.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(object))]   // the injected _links/_actions/_templates properties are declared as object
[JsonSerializable(typeof(Dictionary<string, HalLinkValue>))]
[JsonSerializable(typeof(Dictionary<string, HalAction>))]
[JsonSerializable(typeof(Dictionary<string, HalFormsTemplate>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(HalLink))]
[JsonSerializable(typeof(List<HalLink>))]
[JsonSerializable(typeof(HalLinkValue))]
[JsonSerializable(typeof(HalAction))]
[JsonSerializable(typeof(HalFormsTemplate))]
[JsonSerializable(typeof(HalFormsProperty))]
[JsonSerializable(typeof(HalFormsOptions))]
[JsonSerializable(typeof(HalFormsOption))]
internal sealed partial class CairnJsonContext : JsonSerializerContext;
