using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

/// <summary>A captured value-type brush that records compute work over fixed input height pages.</summary>
public interface IHeightBrushGpu
{
    HeightRegion Range { get; }
    int ReadBorder { get; }
    void Record(ref HeightBrushGpuContext context);
}

/// <summary>Adds a height delta using the terrain_height_delta Slang shader.</summary>
public readonly record struct HeightDeltaBrushGpu(Shader Shader, HeightRegion Range, float Delta) : IHeightBrushGpu
{
    public int ReadBorder => 0;

    public void Record(ref HeightBrushGpuContext context)
    {
        var parameters = new HeightDeltaParameters(context.Grid.PageSize,
            context.SourcePageX, context.SourcePageZ, context.SourcePageWidth,
            context.Page.MinX, context.Page.MinZ, context.Range.MinX, context.Range.MinZ,
            context.Range.MaxX, context.Range.MaxZ, context.InitialHeight, Delta);
        context.AddCompute(Shader, "AddHeightDelta", parameters,
            new((uint)(context.Grid.PageSize + 7) / 8, (uint)(context.Grid.PageSize + 7) / 8, 1));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct HeightDeltaParameters(int PageSize,
        int SourcePageX, int SourcePageZ, int SourcePageWidth,
        int PageX, int PageZ, int MinX, int MinZ, int MaxX, int MaxZ,
        float InitialHeight, float Delta);
}

/// <summary>GPU inputs and output for one page of a captured height edit.</summary>
public ref struct HeightBrushGpuContext
{
    private readonly RenderTerrainData _data;
    private RenderGraphFrame _frame;
    private RenderGraphFrameExtension _extension;
    private readonly GraphBufferSlice _source;
    private readonly GraphBufferSlice _pageTable;
    private readonly GraphBufferId _output;

    internal HeightBrushGpuContext(RenderTerrainData data, RenderGraphFrame frame, RenderGraphFrameExtension extension,
        HeightGrid grid, HeightRegion range, HeightRegion page, float initialHeight, HeightGpuInput input,
        GraphBufferId output, GraphPassId previous)
    {
        _data = data;
        _frame = frame;
        _extension = extension;
        Grid = grid;
        Range = range;
        Page = page;
        InitialHeight = initialHeight;
        SourcePageX = input.MinPageX;
        SourcePageZ = input.MinPageZ;
        SourcePageWidth = input.PageWidth;
        _source = input.Samples;
        _pageTable = input.PageTable;
        _output = output;
        LastPass = previous;
    }

    public HeightGrid Grid { get; }
    public HeightRegion Range { get; }
    public HeightRegion Page { get; }
    public float InitialHeight { get; }
    public int SourcePageX { get; }
    public int SourcePageZ { get; }
    public int SourcePageWidth { get; }
    internal GraphPassId LastPass { get; private set; }

    /// <summary>Records a kernel with parameters, source samples, page offsets and the output page.</summary>
    public GraphPassId AddCompute<TParameters>(Shader shader, string entryPoint, TParameters parameters,
        DispatchArguments dispatch) where TParameters : unmanaged
    {
        LinkedComputePipeline pipeline = _data.GetPipeline(shader, entryPoint);
        var state = new HeightComputeData(pipeline, _frame.Upload(parameters, BufferUsages.Constant),
            _frame.CreateBufferSrv(_source, structureStride: sizeof(float)),
            _frame.CreateBufferSrv(_pageTable, structureStride: sizeof(int)),
            _frame.CreateBufferUav(_output, structureStride: sizeof(float)), dispatch);
        GraphPassId pass = _extension.AddComputePass("Edit terrain height", PassQueueSelection.AnyCompatible(), state,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref HeightComputeData data) =>
            {
                access.SetPipeline(data.Pipeline.Pipeline);
                access.SetParameterBlock(data.Pipeline.ParameterLayout);
                access.Bind(data.Parameters, PipelineSync.ComputeShading);
                access.Bind(data.Source, PipelineSync.ComputeShading);
                access.Bind(data.PageTable, PipelineSync.ComputeShading);
                access.Bind(data.Output, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
            },
            static (ref ComputePassCommandScope commands, in HeightComputeData data) => commands.Dispatch(data.Dispatch));
        if (LastPass != default) _extension.OrderAfter(pass, LastPass);
        LastPass = pass;
        return pass;
    }

    private readonly record struct HeightComputeData(LinkedComputePipeline Pipeline, GraphBufferSlice Parameters,
        GraphBufferSrvId Source, GraphBufferSrvId PageTable, GraphBufferUavId Output, DispatchArguments Dispatch);
}
