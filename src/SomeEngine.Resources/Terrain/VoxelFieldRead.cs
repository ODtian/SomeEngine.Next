using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>Read-only signed samples from one fixed field revision.</summary>
public sealed class VoxelFieldRead
{
    private readonly VoxelFieldData _data;
    private readonly float _initialValue;

    internal VoxelFieldRead(VoxelGrid grid, float initialValue, VoxelFieldData data, VoxelRegion range)
    {
        Grid = grid;
        _initialValue = initialValue;
        _data = data;
        Range = range;
    }

    public VoxelGrid Grid { get; }
    public VoxelRegion Range { get; }
    public ulong Revision => _data.Edit.Revision;

    /// <summary>Declares reads of this version's pages in the requested region.</summary>
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler)
    {
        int size = Grid.PageSize;
        var accesses = new List<JobResourceAccess>();
        for (int z = Grid.PageCoordinate(Range.MinZ); z * size < Range.MaxZ; z++)
        for (int y = Grid.PageCoordinate(Range.MinY); y * size < Range.MaxY; y++)
        for (int x = Grid.PageCoordinate(Range.MinX); x * size < Range.MaxX; x++)
            if (_data.Pages.TryGetValue((x, y, z), out TerrainFieldPage? page))
                accesses.Add(JobResourceAccess.Read(scheduler, page.Samples!));
        return accesses.ToArray();
    }

    /// <summary>Reads a global sample coordinate, including the initial value in untouched regions.</summary>
    public float this[int x, int y, int z]
    {
        get
        {
            int size = Grid.PageSize;
            int pageX = Grid.PageCoordinate(x);
            int pageY = Grid.PageCoordinate(y);
            int pageZ = Grid.PageCoordinate(z);
            return _data.Pages.TryGetValue((pageX, pageY, pageZ), out TerrainFieldPage? page)
                ? page.Samples![((z - pageZ * size) * size + y - pageY * size) * size + x - pageX * size]
                : _initialValue;
        }
    }

    /// <summary>Trilinearly samples a terrain-local position whose eight lattice samples are in this region.</summary>
    public float Sample(Vector3 position)
    {
        Vector3 coordinates = position / Grid.SampleSpacing;
        int x = (int)MathF.Floor(coordinates.X);
        int y = (int)MathF.Floor(coordinates.Y);
        int z = (int)MathF.Floor(coordinates.Z);
        Vector3 fraction = coordinates - new Vector3(x, y, z);

        float lowYLowZ = float.Lerp(this[x, y, z], this[x + 1, y, z], fraction.X);
        float highYLowZ = float.Lerp(this[x, y + 1, z], this[x + 1, y + 1, z], fraction.X);
        float lowYHighZ = float.Lerp(this[x, y, z + 1], this[x + 1, y, z + 1], fraction.X);
        float highYHighZ = float.Lerp(this[x, y + 1, z + 1], this[x + 1, y + 1, z + 1], fraction.X);
        float lowZ = float.Lerp(lowYLowZ, highYLowZ, fraction.Y);
        float highZ = float.Lerp(lowYHighZ, highYHighZ, fraction.Y);
        return float.Lerp(lowZ, highZ, fraction.Z);
    }
}
