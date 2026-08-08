namespace SomeEngine.Render.Cluster;

/// <summary>
/// Identifies one append-only Cluster residency epoch. Page ids and GPU fault readbacks are valid
/// only inside the epoch that allocated them; the id prevents delayed readback from aliasing a
/// replacement epoch's page namespace.
/// </summary>
internal readonly record struct ClusterEpochId(ulong Value)
{
    public bool IsValid => Value != 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
