namespace Cairn.AspNetCore;

/// <summary>
/// The names Cairn publishes telemetry under. Subscribe with OpenTelemetry via
/// <c>AddSource(CairnDiagnostics.ActivitySourceName)</c> and <c>AddMeter(CairnDiagnostics.MeterName)</c>.
/// </summary>
public static class CairnDiagnostics
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> name for Cairn's compute-stage spans.</summary>
    public const string ActivitySourceName = "Cairn.AspNetCore";

    /// <summary>
    /// The <see cref="System.Diagnostics.Metrics.Meter"/> name for Cairn's counters:
    /// <c>cairn.resources.linked</c>, <c>cairn.links.computed</c>, <c>cairn.affordances.computed</c>,
    /// <c>cairn.links.unresolved</c> (targets dropped in <see cref="LinkResolutionMode.Lax"/> mode), and
    /// <c>cairn.hypermedia.unemitted</c> (computed hypermedia the serializer never picked up).
    /// </summary>
    public const string MeterName = "Cairn.AspNetCore";
}
