using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cairn.AspNetCore.Internal;

/// <summary>Cairn's process-wide telemetry instruments (see <see cref="CairnDiagnostics"/> for the public names).</summary>
internal static class CairnTelemetry
{
    public static readonly ActivitySource Source = new(CairnDiagnostics.ActivitySourceName);

    private static readonly Meter Meter = new(CairnDiagnostics.MeterName);

    public static readonly Counter<long> ResourcesLinked = Meter.CreateCounter<long>(
        "cairn.resources.linked", unit: "{resource}", description: "Resource instances hypermedia was computed for.");

    public static readonly Counter<long> LinksComputed = Meter.CreateCounter<long>(
        "cairn.links.computed", unit: "{link}", description: "Links computed onto resources.");

    public static readonly Counter<long> AffordancesComputed = Meter.CreateCounter<long>(
        "cairn.affordances.computed", unit: "{affordance}", description: "Affordances computed onto resources.");

    public static readonly Counter<long> LinksUnresolved = Meter.CreateCounter<long>(
        "cairn.links.unresolved", unit: "{link}", description: "Link targets that failed to resolve and were dropped (Lax mode).");

    public static readonly Counter<long> HypermediaUnemitted = Meter.CreateCounter<long>(
        "cairn.hypermedia.unemitted", unit: "{resource_type}", description: "Resource types whose computed hypermedia was never emitted by the serializer.");
}
