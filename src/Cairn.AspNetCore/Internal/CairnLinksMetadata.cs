namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Marks an endpoint (or MVC action) as having opted into Cairn hypermedia via <c>WithLinks()</c> or
/// <c>[CairnLinks]</c>. An endpoint filter or result filter alone is invisible to the OpenAPI/Swagger
/// document generators, so this discoverable marker lets them tell which endpoints actually project links —
/// only those advertise the negotiable <c>hal+json</c>/<c>hal-forms+json</c> media types. Matched by
/// interface full name from Cairn.OpenApi and Cairn.Swashbuckle, which do not reference Cairn.AspNetCore.
/// </summary>
internal interface ICairnLinksMetadata;

/// <summary>The endpoint-metadata marker added by <c>WithLinks()</c> (see <see cref="ICairnLinksMetadata"/>).</summary>
internal sealed class CairnLinksMetadata : ICairnLinksMetadata
{
    public static readonly CairnLinksMetadata Instance = new();

    private CairnLinksMetadata()
    {
    }
}
