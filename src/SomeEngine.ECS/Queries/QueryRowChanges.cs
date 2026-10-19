using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Queries;

/// <summary>Row-write changes borrowed from one query chunk.</summary>
public interface IQueryRowChanges
{
    bool HasChanged(int row);
}

/// <summary>The existing per-row write versions and their query baseline.</summary>
public readonly ref struct QueryRowChanges : IQueryRowChanges
{
    private readonly ReadOnlySpan<uint> _versions;
    private readonly uint _lastSystemVersion;

    internal QueryRowChanges(ReadOnlySpan<uint> versions, uint lastSystemVersion)
    {
        _versions = versions;
        _lastSystemVersion = lastSystemVersion;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasChanged(int row) =>
        !_versions.IsEmpty && VersionClock.IsNewer(_versions[row], _lastSystemVersion);
}
