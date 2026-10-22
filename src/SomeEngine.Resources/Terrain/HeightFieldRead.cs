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

    public float this[int x, int z]
    {
        get
        {
            int size = Grid.PageSize;
            return _data.Pages.TryGetValue((x / size, z / size), out float[]? page)
                ? page[(z % size) * size + x % size]
                : _initialHeight;
        }
    }
}
