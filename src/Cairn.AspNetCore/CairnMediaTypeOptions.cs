using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore;

/// <summary>
/// The media types Cairn negotiates its wire formats by and labels responses with. Every token can be
/// overridden; the defaults are the conventional ones. In the default configuration a plain
/// <c>application/json</c> request yields Cairn's flat <c>_links</c>/<c>_actions</c> shape. When hypermedia is
/// made opt-in (<see cref="CairnOptions.DefaultFormat"/> = <see cref="HypermediaFormat.None"/>),
/// <see cref="Json"/> instead yields the bare resource and <see cref="Cairn"/> is the door a client uses to
/// ask for the flat shape by name.
/// </summary>
/// <remarks>All four tokens must be distinct, concrete media types (no wildcards or parameters); this is
/// validated when the host starts.</remarks>
public sealed class CairnMediaTypeOptions
{
    private string _json = "application/json";
    private string _cairn = "application/vnd.cairn+json";
    private string _hal = "application/hal+json";
    private string _halForms = "application/prs.hal-forms+json";

    /// <summary>
    /// The generic JSON media type. Selects (and labels) Cairn's flat shape normally, or the bare resource when
    /// hypermedia is opt-in. Default <c>application/json</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is null, whitespace, or not a concrete media type.</exception>
    public string Json
    {
        get => _json;
        set => _json = Validate(value, nameof(Json));
    }

    /// <summary>
    /// An explicit media type that always selects Cairn's flat <c>_links</c>/<c>_actions</c> shape — the door
    /// that stays open when <see cref="Json"/> is reserved for the bare resource under opt-in. Recognized in
    /// every mode. Default <c>application/vnd.cairn+json</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is null, whitespace, or not a concrete media type.</exception>
    public string Cairn
    {
        get => _cairn;
        set => _cairn = Validate(value, nameof(Cairn));
    }

    /// <summary>The media type that selects (and labels) HAL. Default <c>application/hal+json</c>.</summary>
    /// <exception cref="ArgumentException">The value is null, whitespace, or not a concrete media type.</exception>
    public string Hal
    {
        get => _hal;
        set => _hal = Validate(value, nameof(Hal));
    }

    /// <summary>The media type that selects (and labels) HAL-FORMS. Default <c>application/prs.hal-forms+json</c>.</summary>
    /// <exception cref="ArgumentException">The value is null, whitespace, or not a concrete media type.</exception>
    public string HalForms
    {
        get => _halForms;
        set => _halForms = Validate(value, nameof(HalForms));
    }

    // The four tokens, for the startup distinctness/collision check.
    internal IReadOnlyList<(string Name, string Value)> All =>
        [(nameof(Json), _json), (nameof(Cairn), _cairn), (nameof(Hal), _hal), (nameof(HalForms), _halForms)];

    // Same rule as CairnOptions.AddFormatter: the token is matched exactly during Accept negotiation and written
    // as the response Content-Type, so it must be a concrete type/subtype pair without wildcards or parameters.
    private static string Validate(string mediaType, string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType, property);
        if (!MediaTypeHeaderValue.TryParse(mediaType, out var parsed)
            || parsed.MatchesAllTypes
            || parsed.MatchesAllSubTypes
            || parsed.Parameters.Count > 0)
        {
            throw new ArgumentException(
                $"'{mediaType}' is not a usable media type for MediaTypes.{property}. It must be a concrete type/subtype pair without wildcards or parameters (e.g. \"application/vnd.acme+json\"): it is matched exactly during Accept negotiation and written as the response Content-Type.",
                property);
        }

        return mediaType;
    }
}
