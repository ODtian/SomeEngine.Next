using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>GPU surface extraction, including the capacity needed before generated counts are available.</summary>
public interface ITerrainMesherGpu
{
    int ReadBorder { get; }
    ClusterGpuMeshCapacity GetCapacity(TerrainChunkBounds bounds);
    GraphPassId Record(ref TerrainGpuMesherContext context);
}

/// <summary>Regular Transvoxel Marching Cubes cells with the selected scalar-source entry.</summary>
public sealed class MarchingCubesTerrainMesherGpu(Shader shader, string entryPoint = "ExtractHeightSurface") : ITerrainMesherGpu
{
    public int ReadBorder => 1;
    public ClusterGpuMeshCapacity GetCapacity(TerrainChunkBounds bounds)
    {
        uint cells = (uint)(bounds.CellCount * bounds.CellCount * bounds.CellCount);
        return new(cells * 12, cells * 5);
    }

    public GraphPassId Record(ref TerrainGpuMesherContext context) =>
        context.AddCompute(shader, entryPoint, new(1, 1, 1));
}

/// <summary>Records surface kernels over the captured source and common position/sample/index/count outputs.</summary>
public ref struct TerrainGpuMesherContext
{
    private readonly RenderTerrainGeometry _data;
    private RenderGraphFrame _frame;
    private readonly IReadOnlyDictionary<string, GraphParameterResourceBinding> _bindings;

    internal TerrainGpuMesherContext(RenderTerrainGeometry data, RenderGraphFrame frame, TerrainChunkBounds bounds,
        IReadOnlyDictionary<string, GraphParameterResourceBinding> bindings)
    {
        _data = data;
        _frame = frame;
        Bounds = bounds;
        _bindings = bindings;
    }

    public TerrainChunkBounds Bounds { get; }
    public GraphPassId AddCompute(Shader shader, string entryPoint, DispatchArguments dispatch) =>
        _data.AddCompute(ref _frame, shader, entryPoint, _bindings, dispatch);
}
