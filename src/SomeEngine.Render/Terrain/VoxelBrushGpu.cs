using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

/// <summary>A captured value-type brush that records compute work over fixed input voxel pages.</summary>
public interface IVoxelBrushGpu
{
    VoxelRegion Range { get; }
    int ReadBorder { get; }
    void Record(ref VoxelBrushGpuContext context);
}

/// <summary>Adds a scalar delta with linear radial falloff in terrain-local metres.</summary>
public readonly record struct VoxelSphereDeltaBrushGpu : IVoxelBrushGpu
{
    private readonly VoxelSphereDeltaBrush _brush;

    public VoxelSphereDeltaBrushGpu(Shader shader, VoxelGrid grid, Vector3 center, float radius, float delta)
    {
        Shader = shader;
        _brush = new VoxelSphereDeltaBrush(grid, center, radius, delta);
    }

    public Shader Shader { get; }
    public Vector3 Center => _brush.Center;
    public float Radius => _brush.Radius;
    public float Delta => _brush.Delta;
    public VoxelRegion Range => _brush.Range;
    public int ReadBorder => 0;

    public void Record(ref VoxelBrushGpuContext context)
    {
        var parameters = new VoxelDeltaParameters(context.Grid.PageSize,
            context.SourcePageX, context.SourcePageY, context.SourcePageZ, context.SourcePageWidth, context.SourcePageHeight,
            context.Page.MinX, context.Page.MinY, context.Page.MinZ,
            context.Range.MinX, context.Range.MinY, context.Range.MinZ,
            context.Range.MaxX, context.Range.MaxY, context.Range.MaxZ, context.InitialValue,
            context.Grid.SampleSpacing, Center.X, Center.Y, Center.Z, Radius, Delta);
        uint groups = (uint)(context.Grid.PageSize + 3) / 4;
        context.AddCompute(Shader, "AddVoxelSphereDelta", parameters, new(groups, groups, groups));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct VoxelDeltaParameters(int PageSize,
        int SourcePageX, int SourcePageY, int SourcePageZ, int SourcePageWidth, int SourcePageHeight,
        int PageX, int PageY, int PageZ, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ,
        float InitialValue, float SampleSpacing, float CenterX, float CenterY, float CenterZ, float Radius, float Delta);
}

/// <summary>GPU inputs and output for one page of a captured voxel edit.</summary>
public ref struct VoxelBrushGpuContext
{
    private readonly RenderTerrainData _data;
    private RenderGraphFrame _frame;
    private RenderGraphFrameExtension _extension;
    private readonly GraphBufferSlice _source;
    private readonly GraphBufferSlice _pageTable;
    private readonly GraphBufferId _output;

    internal VoxelBrushGpuContext(RenderTerrainData data, RenderGraphFrame frame, RenderGraphFrameExtension extension,
        VoxelGrid grid, VoxelRegion range, VoxelRegion page, float initialValue, VoxelGpuInput input,
        GraphBufferId output, GraphPassId previous)
    {
        _data = data;
        _frame = frame;
        _extension = extension;
        Grid = grid;
        Range = range;
        Page = page;
        InitialValue = initialValue;
        SourcePageX = input.MinPageX;
        SourcePageY = input.MinPageY;
        SourcePageZ = input.MinPageZ;
        SourcePageWidth = input.PageWidth;
        SourcePageHeight = input.PageHeight;
        _source = input.Samples;
        _pageTable = input.PageTable;
        _output = output;
        LastPass = previous;
    }

    public VoxelGrid Grid { get; }
    public VoxelRegion Range { get; }
    public VoxelRegion Page { get; }
    public float InitialValue { get; }
    public int SourcePageX { get; }
    public int SourcePageY { get; }
    public int SourcePageZ { get; }
    public int SourcePageWidth { get; }
    public int SourcePageHeight { get; }
    public GraphBufferSlice SourceSamples => _source;
    public GraphBufferSlice SourcePageOffsets => _pageTable;
    public GraphBufferId Output => _output;
    public RenderGraphFrame Graph => _frame;
    internal GraphPassId LastPass { get; private set; }

    /// <summary>Records a kernel with parameters, source samples, page offsets and the output page.</summary>
    public GraphPassId AddCompute<TParameters>(Shader shader, string entryPoint, TParameters parameters,
        DispatchArguments dispatch) where TParameters : unmanaged
        => AddCompute(shader, entryPoint, parameters, dispatch,
        [
            GraphParameterResourceBinding.ReadOnlyBuffer(
                _frame.CreateBufferSrv(_source, structureStride: sizeof(float)), PipelineSync.ComputeShading),
            GraphParameterResourceBinding.ReadOnlyBuffer(
                _frame.CreateBufferSrv(_pageTable, structureStride: sizeof(int)), PipelineSync.ComputeShading),
            GraphParameterResourceBinding.WritableBuffer(
                _frame.CreateBufferUav(_output, structureStride: sizeof(float)), PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete),
        ]);

    /// <summary>Records a stage with explicit shader bindings after its parameter constants and previous stages.</summary>
    public GraphPassId AddCompute<TParameters>(Shader shader, string entryPoint, TParameters parameters,
        DispatchArguments dispatch, scoped ReadOnlySpan<GraphParameterResourceBinding> bindings) where TParameters : unmanaged
    {
        GraphPassId pass = TerrainBrushCompute.Add(_data, ref _frame, ref _extension, LastPass,
            shader, entryPoint, parameters, dispatch, bindings);
        LastPass = pass;
        return pass;
    }
}
