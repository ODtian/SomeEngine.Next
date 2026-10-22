using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>Height samples H(x,z) belonging to one terrain.</summary>
public sealed class HeightField
{
    internal HeightFieldData Data { get; set; }

    internal HeightField(Terrain terrain, int id, string name, HeightGrid grid, float initialHeight,
        HeightFieldData data)
    {
        Terrain = terrain;
        Id = id;
        Name = name;
        Grid = grid;
        InitialHeight = initialHeight;
        Data = data;
    }

    public Terrain Terrain { get; }
    public int Id { get; }
    public string Name { get; }
    public HeightGrid Grid { get; }
    public float InitialHeight { get; }
    public ulong Revision { get { lock (Terrain.Gate) return Data.Edit.Revision; } }

    /// <summary>Copies brush parameters and queues the edit after this field's previous edit.</summary>
    /// <example>
    /// These are demonstration values: 1 metre sample spacing, 4 by 4 sample pages,
    /// and a 3 metre height increase over a 4 by 3 sample region.
    /// <code>
    /// using var scheduler = new Scheduler();
    /// var terrain = new Terrain(new TerrainEditing(scheduler));
    /// HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 6, 4));
    /// TerrainEdit edit = height.EditCpu(new HeightDeltaBrush(new HeightRegion(2, 1, 4, 3), 3f));
    /// await edit.WaitAsync();
    /// HeightFieldRead data = await height.ReadCpuAsync(height.Grid.Range);
    /// </code>
    /// </example>
    public TerrainEdit EditCpu<TBrush>(TBrush brush, JobHandle dependency = default)
        where TBrush : struct, IHeightBrushCpu => Terrain.Editing.EditCpu(this, brush, dependency);

    /// <summary>Captures the submitted version on entry and returns its requested samples when ready.</summary>
    public ValueTask<HeightFieldRead> ReadCpuAsync(HeightRegion range)
    {
        lock (Terrain.Gate)
            return ReadCpuAsync(Data, range.Intersect(Grid.Range));
    }

    private async ValueTask<HeightFieldRead> ReadCpuAsync(HeightFieldData data, HeightRegion range)
    {
        await data.Edit.WaitAsync().ConfigureAwait(false);
        return new HeightFieldRead(Grid, InitialHeight, data, range);
    }
}

internal sealed class HeightFieldData(
    Dictionary<(int X, int Z), float[]> pages, TerrainEdit edit)
{
    internal Dictionary<(int X, int Z), float[]> Pages { get; } = pages;
    internal TerrainEdit Edit { get; } = edit;
}
