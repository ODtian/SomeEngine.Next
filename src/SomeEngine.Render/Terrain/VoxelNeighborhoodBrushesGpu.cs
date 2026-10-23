using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

/// <summary>Computes the fixed source mean, then blends it into a new voxel page.</summary>
public readonly record struct VoxelSmoothBrushGpu(Shader Shader, VoxelSmoothBrush Brush) : IVoxelBrushGpu
{
    public VoxelSmoothBrushGpu(Shader shader, VoxelGrid grid, Vector3 center, float radius, int neighborRadius, float strength = 1f)
        : this(shader, new VoxelSmoothBrush(grid, center, radius, neighborRadius, strength)) { }

    public VoxelRegion Range => Brush.Range;
    public int ReadBorder => Brush.ReadBorder;

    public void Record(ref VoxelBrushGpuContext context)
    {
        VoxelNeighborhoodParameters parameters = VoxelNeighborhoodParameters.Create(context, Brush.Center,
            Brush.Radius, Brush.Strength, Brush.NeighborRadius, default);
        RenderGraphFrame graph = context.Graph;
        ulong size = (ulong)context.Grid.PageSize;
        GraphBufferId mean = graph.CreateBuffer(new(size * size * size * sizeof(float),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Terrain voxel mean"));
        GraphBufferSrvId source = graph.CreateBufferSrv(context.SourceSamples, structureStride: sizeof(float));
        GraphBufferSrvId offsets = graph.CreateBufferSrv(context.SourcePageOffsets, structureStride: sizeof(int));
        GraphBufferUavId meanView = graph.CreateBufferUav(mean, structureStride: sizeof(float));
        GraphBufferUavId output = graph.CreateBufferUav(context.Output, structureStride: sizeof(float));
        uint groups = (uint)(context.Grid.PageSize + 3) / 4;
        var dispatch = new DispatchArguments(groups, groups, groups);
        context.AddCompute(Shader, "AverageVoxel", parameters, dispatch,
        [
            GraphParameterResourceBinding.ReadOnlyBuffer(source, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.ReadOnlyBuffer(offsets, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.WritableBuffer(meanView, PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete),
            GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav),
        ]);
        context.AddCompute(Shader, "SmoothVoxel", parameters, dispatch,
        [
            GraphParameterResourceBinding.ReadOnlyBuffer(source, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.ReadOnlyBuffer(offsets, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.WritableBuffer(meanView, PipelineSync.ComputeShading, GraphAccessMode.Read),
            GraphParameterResourceBinding.WritableBuffer(output, PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete),
        ]);
    }
}

/// <summary>Blends a fixed source voxel page toward the CPU brush's normalized signed plane.</summary>
public readonly record struct VoxelFlattenBrushGpu(Shader Shader, VoxelFlattenBrush Brush) : IVoxelBrushGpu
{
    public VoxelFlattenBrushGpu(Shader shader, VoxelGrid grid, Vector3 center, float radius, Plane plane, float strength = 1f)
        : this(shader, new VoxelFlattenBrush(grid, center, radius, plane, strength)) { }

    public VoxelRegion Range => Brush.Range;
    public int ReadBorder => Brush.ReadBorder;

    public void Record(ref VoxelBrushGpuContext context)
    {
        VoxelNeighborhoodParameters parameters = VoxelNeighborhoodParameters.Create(context, Brush.Center,
            Brush.Radius, Brush.Strength, 0, Brush.TargetPlane);
        RenderGraphFrame graph = context.Graph;
        uint groups = (uint)(context.Grid.PageSize + 3) / 4;
        context.AddCompute(Shader, "FlattenVoxel", parameters, new(groups, groups, groups),
        [
            GraphParameterResourceBinding.ReadOnlyBuffer(
                graph.CreateBufferSrv(context.SourceSamples, structureStride: sizeof(float)), PipelineSync.ComputeShading),
            GraphParameterResourceBinding.ReadOnlyBuffer(
                graph.CreateBufferSrv(context.SourcePageOffsets, structureStride: sizeof(int)), PipelineSync.ComputeShading),
            GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav),
            GraphParameterResourceBinding.WritableBuffer(
                graph.CreateBufferUav(context.Output, structureStride: sizeof(float)), PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete),
        ]);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct VoxelNeighborhoodParameters(int PageSize, int NeighborRadius,
    int SourcePageX, int SourcePageY, int SourcePageZ, int SourcePageWidth, int SourcePageHeight,
    int PageX, int PageY, int PageZ, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ,
    float InitialValue, float SampleSpacing, float CenterX, float CenterY, float CenterZ, float Radius, float Strength,
    float PlaneX, float PlaneY, float PlaneZ, float PlaneD)
{
    internal static VoxelNeighborhoodParameters Create(VoxelBrushGpuContext context, Vector3 center,
        float radius, float strength, int neighborRadius, Plane plane) => new(context.Grid.PageSize, neighborRadius,
        context.SourcePageX, context.SourcePageY, context.SourcePageZ, context.SourcePageWidth, context.SourcePageHeight,
        context.Page.MinX, context.Page.MinY, context.Page.MinZ,
        context.Range.MinX, context.Range.MinY, context.Range.MinZ,
        context.Range.MaxX, context.Range.MaxY, context.Range.MaxZ,
        context.InitialValue, context.Grid.SampleSpacing, center.X, center.Y, center.Z, radius, strength,
        plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D);
}
