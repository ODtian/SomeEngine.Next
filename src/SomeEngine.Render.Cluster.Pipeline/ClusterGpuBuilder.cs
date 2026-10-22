using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Frame;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Float3 positions, opaque user attributes, uint indices, and two uint counts (vertices, indices).</summary>
public readonly record struct ClusterGpuMeshInput(
    GraphBufferSlice Positions, GraphBufferSlice Attributes, GraphBufferSlice Indices, GraphBufferSlice Counts);

/// <summary>The compute entry calls BuildCluster with the user's IClusterVertexEncode implementation.</summary>
public sealed record ClusterGpuVertexEncoder(Shader Shader, string EntryPoint, string VertexEncoding,
    uint MaximumVertexBlobBytes);

/// <summary>Builds one Cluster with up to 64 vertices and 124 triangles in shared Cluster residency.</summary>
public sealed class ClusterGpuBuilder : IDisposable
{
    public const uint MaximumVertices = 64;
    public const uint MaximumTriangles = 124;
    private readonly ClusterRenderResources _resources;
    private readonly ClusterGpuVertexEncoder _encoder;
    private readonly ClusterComputePipeline _pipeline;
    private readonly ClusterLinkedStageLayout _bindings;
    private readonly ulong _encoding;

    public ClusterGpuBuilder(IGraphicsBackend backend, Device device, ClusterRenderResources resources,
        ClusterGpuVertexEncoder encoder)
    {
        _resources = resources;
        _encoder = encoder;
        _pipeline = ClusterComputePipeline.Create(backend, device, encoder.Shader, encoder.EntryPoint,
            "Build Cluster geometry");
        _bindings = ClusterLinkedStageLayout.Create(_pipeline.ParameterLayout);
        _encoding = VertexEncodingCompatibility.Hash(encoder.VertexEncoding);
    }

    /// <summary>Reserves device storage. Record the build before the geometry is drawn in this frame.</summary>
    public ClusterGeometry Reserve(in RenderPrepareSystemContext context, ulong revision, Bounds? bounds = null) =>
        _resources.ReserveGpu(context, checked(144 + _encoder.MaximumVertexBlobBytes + MaximumTriangles * 3 + 3),
            _encoder.VertexEncoding, revision, bounds);

    /// <summary>Removes the geometry's ranges from future reads and recycles them after submitted uses complete.</summary>
    public void Release(in RenderPrepareSystemContext context, ClusterGeometry geometry) =>
        _resources.ReleaseGpu(context, geometry);

    /// <summary>Records encoding and BVH writes, returning the pass whose completion makes the result ready.</summary>
    public GraphPassId Record(ref RenderFrameSystemContext context, ClusterGeometry geometry,
        in ClusterGpuMeshInput input, byte materialIndex = 0)
    {
        using RenderFrameUseLease use = _resources.UseGpu(context.ActiveFrame);
        RenderGraphFrame graph = context.Graph;
        ClusterGpuAllocation allocation = _resources.GetGpu(geometry);
        ClusterResidencyBinding buffers = _resources.GpuBuffers;
        GraphBufferId pages = graph.Import(buffers.PageHeap, []);
        GraphBufferId bvh = graph.Import(buffers.Bvh, []);
        var uniforms = new BuildUniforms(allocation.PageOffset, allocation.PageBytes,
            (uint)_encoding, (uint)(_encoding >> 32), materialIndex);
        GraphBufferSlice parameters = graph.Upload(uniforms, BufferUsages.Constant);
        GraphBufferSrvId positions = graph.CreateBufferSrv(input.Positions);
        GraphBufferSrvId attributes = graph.CreateBufferSrv(input.Attributes);
        GraphBufferSrvId indices = graph.CreateBufferSrv(input.Indices);
        GraphBufferSrvId counts = graph.CreateBufferSrv(input.Counts);
        GraphBufferUavId outputPages = graph.CreateBufferUav(pages,
            new BufferRange(allocation.PageOffset, allocation.PageBytes));
        GraphBufferUavId outputBvh = graph.CreateBufferUav(bvh,
            new BufferRange(allocation.Bvh.Offset, allocation.Bvh.Length));
        var bindings = new List<GraphParameterResourceBinding>();
        foreach (ClusterLinkedStageResource resource in _bindings.Resources)
            bindings.Add(resource.Name switch
            {
                "ClusterBuildParameters" => GraphParameterResourceBinding.ConstantBuffer(parameters, PipelineSync.ComputeShading),
                "ClusterBuildPositions" => Read(positions),
                "ClusterBuildAttributes" => Read(attributes),
                "ClusterBuildIndices" => Read(indices),
                "ClusterBuildCounts" => Read(counts),
                "ClusterBuildPages" => Write(outputPages),
                "ClusterBuildBvh" => Write(outputBvh),
                _ => throw new InvalidDataException($"Unknown Cluster build resource '{resource.Name}'."),
            });
        var data = new BuildPass(_pipeline, [.. bindings]);
        GraphPassId pass = graph.AddComputePass("Build Cluster geometry", PassQueueSelection.AnyOfType(QueueType.Graphics),
            data, new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref BuildPass value) =>
            {
                access.SetPipeline(value.Pipeline.Pipeline);
                access.SetParameterBlock(value.Pipeline.ParameterLayout);
                foreach (GraphParameterResourceBinding binding in value.Bindings) access.Bind(binding);
            },
            static (ref ComputePassCommandScope commands, in BuildPass _) => commands.Dispatch(new(1, 1, 1)));
        _resources.AddGeometryBuild(graph.FrameIdentity, pass);
        return pass;

        static GraphParameterResourceBinding Read(GraphBufferSrvId view) =>
            GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading);
        static GraphParameterResourceBinding Write(GraphBufferUavId view) =>
            GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete);
    }

    public void Dispose() => _pipeline.Dispose();

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private readonly record struct BuildUniforms(uint PageOffset, uint PageBytes, uint EncodingLow,
        uint EncodingHigh, uint MaterialIndex);
    private sealed record BuildPass(ClusterComputePipeline Pipeline, GraphParameterResourceBinding[] Bindings);
}
