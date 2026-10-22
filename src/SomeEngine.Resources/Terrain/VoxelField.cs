using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>Signed samples D(x,y,z): negative values are solid and zero is the surface.</summary>
public sealed class VoxelField
{
    internal VoxelFieldData Data { get; set; }

    internal VoxelField(Terrain terrain, int id, string name, VoxelGrid grid, float initialValue,
        VoxelFieldData data)
    {
        Terrain = terrain;
        Id = id;
        Name = name;
        Grid = grid;
        InitialValue = initialValue;
        Data = data;
    }

    public Terrain Terrain { get; }
    public int Id { get; }
    public string Name { get; }
    public VoxelGrid Grid { get; }
    public float InitialValue { get; }
    public ulong Revision { get { lock (Terrain.Gate) return Data.Edit.Revision; } }

    /// <summary>Copies brush parameters and queues the edit after this field's previous edit.</summary>
    /// <example>
    /// These are demonstration values: 1 metre sample spacing, 4 by 4 by 4 sample pages,
    /// a 2 metre radius, and a centre delta of -4 in an initially positive field.
    /// <code>
    /// using var scheduler = new Scheduler();
    /// var terrain = new Terrain(new TerrainEditing(scheduler));
    /// VoxelField field = terrain.CreateVoxelField("cave", new VoxelGrid(4), 2f);
    /// var brush = new VoxelSphereDeltaBrush(field.Grid, new Vector3(-1, 0, 0), 2f, -4f);
    /// TerrainEdit edit = field.EditCpu(brush);
    /// await edit.WaitAsync();
    /// VoxelFieldRead data = await field.ReadCpuAsync(brush.Range);
    /// </code>
    /// </example>
    public TerrainEdit EditCpu<TBrush>(TBrush brush, JobHandle dependency = default)
        where TBrush : struct, IVoxelBrushCpu
    {
        TerrainEdit edit = Terrain.Editing.EditCpu(this, brush, dependency);
        Terrain.NotifyChange(new VoxelFieldChange(this, brush.Range, brush.ReadBorder, edit));
        return edit;
    }

    /// <summary>Captures the submitted version on entry and returns its requested samples when ready.</summary>
    public ValueTask<VoxelFieldRead> ReadCpuAsync(VoxelRegion range)
    {
        lock (Terrain.Gate)
            return CompleteReadAsync(CaptureCpuRead(range));
    }

    internal VoxelFieldCpuRead CaptureCpuRead(VoxelRegion range)
    {
        VoxelFieldData data = Data;
        var samples = new VoxelFieldRead(Grid, InitialValue, data, range);
        return new(samples, data.Edit.Completion, samples.GetReadAccesses(Terrain.Editing.Scheduler));
    }

    private static async ValueTask<VoxelFieldRead> CompleteReadAsync(VoxelFieldCpuRead read)
    {
        await read.Completion.WaitAsync().ConfigureAwait(false);
        return read.Samples;
    }
}

internal sealed class VoxelFieldData(
    Dictionary<(int X, int Y, int Z), float[]> pages, TerrainEdit edit)
{
    internal Dictionary<(int X, int Y, int Z), float[]> Pages { get; } = pages;
    internal TerrainEdit Edit { get; } = edit;
}

internal readonly record struct VoxelFieldCpuRead(
    VoxelFieldRead Samples, JobHandle Completion, JobResourceAccess[] Accesses);
