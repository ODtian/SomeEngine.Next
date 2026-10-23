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
        where TBrush : struct, IHeightBrushCpu
    {
        TerrainEdit edit = Terrain.Editing.EditCpu(this, brush, dependency);
        Terrain.NotifyChange(new HeightFieldChange(this, brush.Range.Intersect(Grid.Range), brush.ReadBorder, edit));
        return edit;
    }

    /// <summary>Captures the submitted version on entry and returns its requested samples when ready.</summary>
    public ValueTask<HeightFieldRead> ReadCpuAsync(HeightRegion range)
    {
        lock (Terrain.Gate)
            return CompleteReadAsync(CaptureCpuRead(range));
    }

    internal HeightFieldCpuRead CaptureCpuRead(HeightRegion range)
    {
        range = range.Intersect(Grid.Range);
        HeightFieldData data = Data;
        var dependencies = new List<JobHandle> { data.Edit.Completion };
        var accesses = new List<JobResourceAccess>();
        int size = Grid.PageSize;
        for (int z = range.MinZ / size; z * size < range.MaxZ; z++)
        for (int x = range.MinX / size; x * size < range.MaxX; x++)
            if (data.Pages.TryGetValue((x, z), out TerrainFieldPage? page))
            {
                TerrainPageRead read = page.ReadCpu(Terrain);
                dependencies.Add(read.Completion);
                accesses.Add(JobResourceAccess.Read(Terrain.Editing.Scheduler, read.Samples));
            }
        return new(new HeightFieldRead(Grid, InitialHeight, data, range),
            Terrain.Editing.Scheduler.CombineDependencies(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dependencies)),
            accesses.ToArray());
    }

    internal JobHandle PrepareGpuRead(HeightRegion range)
    {
        range = range.Intersect(Grid.Range);
        var dependencies = new List<JobHandle> { Data.Edit.Completion };
        int size = Grid.PageSize;
        for (int z = range.MinZ / size; z * size < range.MaxZ; z++)
        for (int x = range.MinX / size; x * size < range.MaxX; x++)
            if (Data.Pages.TryGetValue((x, z), out TerrainFieldPage? page))
                dependencies.Add(page.PrepareGpuRead(Terrain));
        return Terrain.Editing.Scheduler.CombineDependencies(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dependencies));
    }

    private static async ValueTask<HeightFieldRead> CompleteReadAsync(HeightFieldCpuRead read)
    {
        await read.Completion.WaitAsync().ConfigureAwait(false);
        return read.Samples;
    }
}

internal sealed class HeightFieldData(
    Dictionary<(int X, int Z), TerrainFieldPage> pages, TerrainEdit edit)
{
    internal Dictionary<(int X, int Z), TerrainFieldPage> Pages { get; } = pages;
    internal TerrainEdit Edit { get; } = edit;
}

internal readonly record struct HeightFieldCpuRead(
    HeightFieldRead Samples, JobHandle Completion, JobResourceAccess[] Accesses);
