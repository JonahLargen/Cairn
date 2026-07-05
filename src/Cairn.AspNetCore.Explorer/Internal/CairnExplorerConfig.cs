using System.Text.Json.Serialization;

namespace Cairn.AspNetCore.Explorer.Internal;

/// <summary>
/// The bootstrap configuration baked into the served page and read by the UI (<c>JSON.parse</c> of an inline
/// <c>&lt;script type="application/json"&gt;</c>). Serialized through <see cref="CairnExplorerJsonContext"/> so the
/// package stays trim- and AOT-clean.
/// </summary>
/// <param name="EntryPoint">The resource URL the UI loads first.</param>
/// <param name="Title">The masthead / document title.</param>
/// <param name="MediaTypes">The media types the UI negotiates by — read from the app's Cairn configuration so a
/// customized <see cref="CairnMediaTypeOptions"/> is honored.</param>
internal sealed record CairnExplorerConfig(string EntryPoint, string Title, CairnExplorerMediaTypes MediaTypes);

/// <summary>The negotiable media types offered in the explorer's <c>Accept</c> selector.</summary>
internal sealed record CairnExplorerMediaTypes(string HalForms, string Hal, string Cairn, string Json);

/// <summary>Source-generated JSON metadata for the bootstrap config, keeping serialization reflection-free.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CairnExplorerConfig))]
internal sealed partial class CairnExplorerJsonContext : JsonSerializerContext;
