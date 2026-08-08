namespace SomeEngine.Job;

/// <summary>
/// Keeps resource identities alive while a full semantic dependency is pending, without
/// publishing an access into the conflict frontier before its owner can execute.
/// </summary>
internal readonly struct ResourceAccessReservation
{
    internal static readonly ResourceAccessReservation Empty = new(null);

    internal ResourceAccessReservation(ResourceAccessReservationData? data)
    {
        Data = data;
    }

    internal ResourceAccessReservationData? Data { get; }
}

internal sealed class ResourceAccessReservationData
{
    internal ResourceAccessReservationData(
        ResourceManager.ReservedResourceAccess[] accesses,
        int count,
        Type jobType)
    {
        Accesses = accesses;
        Count = count;
        JobType = jobType;
    }

    internal ResourceManager.ReservedResourceAccess[] Accesses { get; }

    internal int Count { get; set; }

    internal Type JobType { get; }

    internal ResourceReservationStatus Status { get; set; }
}

internal enum ResourceReservationStatus : byte
{
    Reserved,
    Activated,
    Cancelled,
}
