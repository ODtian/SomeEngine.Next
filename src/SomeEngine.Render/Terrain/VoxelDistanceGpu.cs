using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

/// <summary>Records distance reconstruction over the fixed source and writes each output page plus its accuracy report.</summary>
public interface IVoxelDistanceGpu
{
    void Record(ref VoxelDistanceGpuContext context);
}

public static class VoxelDistanceGpu
{
    public static TerrainEdit RebuildDistanceGpu(this VoxelField field, VoxelDistanceSettings settings, Shader shader,
        JobHandle dependency = default) => field.RebuildDistanceGpu(settings, new SubdividedVoxelDistanceGpu(shader), dependency);

    public static TerrainEdit RebuildDistanceGpu<TDistance>(this VoxelField field, VoxelDistanceSettings settings,
        TDistance algorithm, JobHandle dependency = default) where TDistance : struct, IVoxelDistanceGpu
    {
        Resources.Terrain terrain = field.Terrain;
        TerrainEdit edit;
        lock (terrain.Gate)
        {
            VoxelFieldData source = field.Data;
            JobHandle input = field.PrepareGpuRead(settings.ReadRegion);
            var completion = new TerrainCompletion(terrain.Editing.Scheduler);
            edit = new(terrain.NextRevision(), completion.Handle);
            var pages = new Dictionary<(int X, int Y, int Z), TerrainFieldPage>(source.Pages);
            var outputs = new List<VoxelPageWrite>();
            VoxelGrid grid = field.Grid;
            int size = grid.PageSize;
            VoxelRegion range = settings.Range;
            for (int z = grid.PageCoordinate(range.MinZ); z * size < range.MaxZ; z++)
            for (int y = grid.PageCoordinate(range.MinY); y * size < range.MaxY; y++)
            for (int x = grid.PageCoordinate(range.MinX); x * size < range.MaxX; x++)
            {
                var page = new TerrainFieldPage(size * size * size, new TerrainGpuPageSource(), completion.Handle, edit.Revision);
                pages[(x, y, z)] = page;
                outputs.Add(new(x, y, z, page));
            }
            terrain.RetainRequest(new VoxelDistanceGpuRequest<TDistance>(terrain.NextRequestSequence(),
                terrain.Editing.Scheduler.CombineDependencies([input, dependency]), completion,
                grid, field.InitialValue, source, settings, outputs.ToArray(), algorithm));
            field.Data = new(pages, edit);
        }
        terrain.NotifyChange(new VoxelFieldChange(field, settings.Range, settings.ReadBorder, edit));
        return edit;
    }
}

/// <summary>A complete output page, including samples outside the written region.</summary>
public readonly record struct VoxelDistanceGpuPage(VoxelRegion Range, GraphBufferId Output);

/// <summary>Shared fixed input and output pages for all stages of one distance reconstruction.</summary>
public ref struct VoxelDistanceGpuContext
{
    private readonly RenderTerrainData _data;
    private RenderGraphFrame _frame;
    private TerrainFrameCommands _extension;
    private readonly VoxelDistanceGpuPage[] _pages;

    internal VoxelDistanceGpuContext(RenderTerrainData data, RenderGraphFrame frame, TerrainFrameCommands extension,
        VoxelGrid grid, float initialValue, VoxelDistanceSettings settings, VoxelGpuInput input,
        VoxelDistanceGpuPage[] pages, GraphBufferId accuracy)
    {
        _data = data;
        _frame = frame;
        _extension = extension;
        _pages = pages;
        Grid = grid;
        InitialValue = initialValue;
        Settings = settings;
        SourceSamples = input.Samples;
        SourcePageOffsets = input.PageTable;
        SourcePageX = input.MinPageX;
        SourcePageY = input.MinPageY;
        SourcePageZ = input.MinPageZ;
        SourcePageWidth = input.PageWidth;
        SourcePageHeight = input.PageHeight;
        Accuracy = accuracy;
    }

    public VoxelGrid Grid { get; }
    public float InitialValue { get; }
    public VoxelDistanceSettings Settings { get; }
    public GraphBufferSlice SourceSamples { get; }
    public GraphBufferSlice SourcePageOffsets { get; }
    public int SourcePageX { get; }
    public int SourcePageY { get; }
    public int SourcePageZ { get; }
    public int SourcePageWidth { get; }
    public int SourcePageHeight { get; }
    public ReadOnlySpan<VoxelDistanceGpuPage> Pages => _pages;
    /// <summary>Two float values in metres: maximum distance error bound, then maximum grid-edge zero crossing shift.</summary>
    public GraphBufferId Accuracy { get; }
    public RenderGraphFrame Graph => _frame;
    internal GraphPassId LastPass { get; private set; }

    public GraphPassId AddCompute<TParameters>(Shader shader, string entryPoint, TParameters parameters,
        DispatchArguments dispatch, scoped ReadOnlySpan<GraphParameterResourceBinding> bindings) where TParameters : unmanaged
    {
        GraphPassId pass = TerrainBrushCompute.Add(_data, ref _frame, ref _extension, LastPass,
            shader, entryPoint, parameters, dispatch, bindings);
        LastPass = pass;
        return pass;
    }
}

internal sealed class VoxelDistanceGpuRequest<TDistance>(long sequence, JobHandle dependency, TerrainCompletion completion,
    VoxelGrid grid, float initialValue, VoxelFieldData source, VoxelDistanceSettings settings,
    VoxelPageWrite[] outputs, TDistance algorithm) : TerrainGpuRequest(sequence, dependency, completion)
    where TDistance : struct, IVoxelDistanceGpu
{
    private readonly float[] _accuracy = new float[2];

    internal override GraphPassId Record(RenderTerrainData data, ref RenderGraphFrame frame,
        ref TerrainFrameCommands extension)
    {
        VoxelGpuInput input = data.CreateVoxelInput(ref frame, ref extension, grid, source, settings.ReadRegion);
        var pages = new VoxelDistanceGpuPage[outputs.Length];
        for (int index = 0; index < outputs.Length; index++)
        {
            VoxelPageWrite page = outputs[index];
            pages[index] = new(new(page.X * grid.PageSize, page.Y * grid.PageSize, page.Z * grid.PageSize,
                grid.PageSize, grid.PageSize, grid.PageSize), data.GetOutput(ref frame, page.Page));
        }
        GraphBufferId accuracy = frame.CreateBuffer(new(2 * sizeof(float), BufferUsages.ShaderWrite | BufferUsages.CopySource,
            "Terrain distance accuracy"));
        var context = new VoxelDistanceGpuContext(data, frame, extension, grid, initialValue, settings, input, pages, accuracy);
        algorithm.Record(ref context);
        GraphPassId read = data.RecordReadback(this, new(accuracy, BufferRange.Whole), _accuracy, ref frame, ref extension);
        extension.OrderAfter(read, context.LastPass);
        return read;
    }

    internal override void CheckResult() => settings.Precision.Check(_accuracy[0], _accuracy[1]);
}
