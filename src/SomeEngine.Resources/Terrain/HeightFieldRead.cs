using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>Read-only samples from one fixed field revision. Coordinates are global sample coordinates.</summary>
public sealed class HeightFieldRead
{
    private readonly HeightFieldData _data;
    private readonly float _initialHeight;

    internal HeightFieldRead(HeightGrid grid, float initialHeight, HeightFieldData data, HeightRegion range)
    {
        Grid = grid;
        _initialHeight = initialHeight;
        _data = data;
        Range = range;
    }

    public HeightGrid Grid { get; }
    public HeightRegion Range { get; }
    public ulong Revision => _data.Edit.Revision;

    /// <summary>Declares reads of this version's pages in the requested range.</summary>
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler)
    {
        int size = Grid.PageSize;
        var accesses = new List<JobResourceAccess>();
        for (int z = Range.MinZ / size; z * size < Range.MaxZ; z++)
        for (int x = Range.MinX / size; x * size < Range.MaxX; x++)
            if (_data.Pages.TryGetValue((x, z), out TerrainFieldPage? page))
                accesses.Add(JobResourceAccess.Read(scheduler, page.Samples!));
        return accesses.ToArray();
    }

    /// <summary>Bilinear height in terrain-local metres, clamped to the field's sample extent.</summary>
    public float Sample(Vector2 position)
    {
        float x = Math.Clamp(position.X / Grid.SampleSpacing, 0, Grid.Width - 1);
        float z = Math.Clamp(position.Y / Grid.SampleSpacing, 0, Grid.Depth - 1);
        int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
        int x1 = Math.Min(x0 + 1, Grid.Width - 1), z1 = Math.Min(z0 + 1, Grid.Depth - 1);
        float lower = float.Lerp(this[x0, z0], this[x1, z0], x - x0);
        float upper = float.Lerp(this[x0, z1], this[x1, z1], x - x0);
        return float.Lerp(lower, upper, z - z0);
    }

    public float this[int x, int z]
    {
        get
        {
            int size = Grid.PageSize;
            return _data.Pages.TryGetValue((x / size, z / size), out TerrainFieldPage? page)
                ? page.Samples![(z % size) * size + x % size]
                : _initialHeight;
        }
    }
}
