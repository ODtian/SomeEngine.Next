using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

/// <summary>Computes the fixed source mean, then blends it into a new height page.</summary>
public readonly record struct HeightSmoothBrushGpu(Shader Shader, HeightSmoothBrush Brush) : IHeightBrushGpu
{
    public HeightSmoothBrushGpu(Shader shader, HeightGrid grid, Vector2 center, float radius, int neighborRadius, float strength = 1f)
        : this(shader, new HeightSmoothBrush(grid, center, radius, neighborRadius, strength)) { }

    public HeightRegion Range => Brush.Range;
    public int ReadBorder => Brush.ReadBorder;

    public void Record(ref HeightBrushGpuContext context)
    {
        HeightNeighborhoodParameters parameters = HeightNeighborhoodParameters.Create(context, Brush.Center,
            Brush.Radius, Brush.Strength, Brush.NeighborRadius, default);
        RenderGraphFrame graph = context.Graph;
        ulong bytes = (ulong)context.Grid.PageSize * (ulong)context.Grid.PageSize * sizeof(float);
        GraphBufferId mean = graph.CreateBuffer(new(bytes, BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Terrain height mean"));
        GraphBufferSrvId source = graph.CreateBufferSrv(context.SourceSamples, structureStride: sizeof(float));
        GraphBufferSrvId offsets = graph.CreateBufferSrv(context.SourcePageOffsets, structureStride: sizeof(int));
        GraphBufferUavId meanView = graph.CreateBufferUav(mean, structureStride: sizeof(float));
        GraphBufferUavId output = graph.CreateBufferUav(context.Output, structureStride: sizeof(float));
        uint groups = (uint)(context.Grid.PageSize + 7) / 8;
        var dispatch = new DispatchArguments(groups, groups, 1);
        context.AddCompute(Shader, "AverageHeight", parameters, dispatch,
        [
            GraphParameterResourceBinding.ReadOnlyBuffer(source, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.ReadOnlyBuffer(offsets, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.WritableBuffer(meanView, PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete),
            GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav),
        ]);
        context.AddCompute(Shader, "SmoothHeight", parameters, dispatch,
        [
            GraphParameterResourceBinding.ReadOnlyBuffer(source, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.ReadOnlyBuffer(offsets, PipelineSync.ComputeShading),
            GraphParameterResourceBinding.WritableBuffer(meanView, PipelineSync.ComputeShading, GraphAccessMode.Read),
            GraphParameterResourceBinding.WritableBuffer(output, PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete),
        ]);
    }
}

/// <summary>Blends a fixed source height page toward the CPU brush's normalized target plane.</summary>
public readonly record struct HeightFlattenBrushGpu(Shader Shader, HeightFlattenBrush Brush) : IHeightBrushGpu
{
    public HeightFlattenBrushGpu(Shader shader, HeightGrid grid, Vector2 center, float radius, Plane plane, float strength = 1f)
        : this(shader, new HeightFlattenBrush(grid, center, radius, plane, strength)) { }

    public HeightRegion Range => Brush.Range;
    public int ReadBorder => Brush.ReadBorder;

    public void Record(ref HeightBrushGpuContext context)
    {
        HeightNeighborhoodParameters parameters = HeightNeighborhoodParameters.Create(context, Brush.Center,
            Brush.Radius, Brush.Strength, 0, Brush.TargetPlane);
        RenderGraphFrame graph = context.Graph;
        uint groups = (uint)(context.Grid.PageSize + 7) / 8;
        context.AddCompute(Shader, "FlattenHeight", parameters, new(groups, groups, 1),
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
internal readonly record struct HeightNeighborhoodParameters(int PageSize, int GridWidth, int GridDepth, int NeighborRadius,
    int SourcePageX, int SourcePageZ, int SourcePageWidth, int PageX, int PageZ,
    int MinX, int MinZ, int MaxX, int MaxZ, float InitialHeight, float SampleSpacing,
    float CenterX, float CenterZ, float Radius, float Strength, float PlaneX, float PlaneY, float PlaneZ, float PlaneD)
{
    internal static HeightNeighborhoodParameters Create(HeightBrushGpuContext context, Vector2 center,
        float radius, float strength, int neighborRadius, Plane plane) => new(context.Grid.PageSize,
        context.Grid.Width, context.Grid.Depth, neighborRadius,
        context.SourcePageX, context.SourcePageZ, context.SourcePageWidth, context.Page.MinX, context.Page.MinZ,
        context.Range.MinX, context.Range.MinZ, context.Range.MaxX, context.Range.MaxZ,
        context.InitialHeight, context.Grid.SampleSpacing, center.X, center.Y, radius, strength,
        plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D);
}
