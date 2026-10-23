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

/// <summary>Maximum source counts used to reserve device storage before generated counts are available.</summary>
public readonly record struct ClusterGpuMeshCapacity(uint VertexCount, uint TriangleCount);

/// <summary>The compute entry calls BuildCluster with the user's IClusterVertexEncode implementation.</summary>
public sealed record ClusterGpuVertexEncoder(Shader Shader, string EntryPoint, string VertexEncoding,
    uint MaximumVertexBlobBytes);

/// <summary>Groups GPU triangles into Cluster pages and a BVH in shared Cluster residency.</summary>
public sealed class ClusterGpuBuilder : IDisposable
{
    public const uint MaximumVertices = 64;
    public const uint MaximumTriangles = 124;
    private readonly ClusterRenderResources _resources;
    private readonly ClusterGpuVertexEncoder _encoder;
    private readonly ClusterComputePipeline _pipeline;
    private readonly ClusterLinkedStageLayout _bindings;
    private readonly ulong _encoding;
    private readonly GraphBufferId _pages;
    private readonly GraphBufferId _bvh;

    public ClusterGpuBuilder(IGraphicsBackend backend, Device device, ClusterRenderResources resources,
        global::SomeEngine.RenderGraph.RenderGraph graph, ClusterGpuVertexEncoder encoder)
    {
        _resources = resources;
        _encoder = encoder;
        _pipeline = ClusterComputePipeline.Create(backend, device, encoder.Shader, encoder.EntryPoint,
            "Build Cluster geometry");
        _bindings = ClusterLinkedStageLayout.Create(_pipeline.ParameterLayout);
        _encoding = VertexEncodingCompatibility.Hash(encoder.VertexEncoding);
        ClusterResidencyBinding buffers = resources.GpuBuffers;
        using RenderGraphEdit edit = graph.BeginEdit();
        _pages = edit.RegisterExternalBuffer(buffers.PageHeap, []);
        _bvh = edit.RegisterExternalBuffer(buffers.Bvh, []);
        edit.Commit();
    }

    /// <summary>Reserves device storage. Record the build before the geometry is drawn in this frame.</summary>
    public ClusterGeometry Reserve(in RenderPrepareSystemContext context, ulong revision, Bounds? bounds = null) =>
        Reserve(context, new(MaximumVertices, MaximumTriangles), revision, bounds);

    /// <summary>Reserves pages and hierarchy nodes for a mesh within the supplied source capacities.</summary>
    public ClusterGeometry Reserve(in RenderPrepareSystemContext context, ClusterGpuMeshCapacity capacity,
        ulong revision, Bounds? bounds = null)
    {
        // A completed group contains at least 21 triangles when each triangle adds three vertices.
        uint trianglesPerPage = capacity.VertexCount <= MaximumVertices ? MaximumTriangles : MaximumVertices / 3;
        uint pages = Math.Max(1u, checked((capacity.TriangleCount + trianglesPerPage - 1u) / trianglesPerPage));
        uint nodes = pages;
        int depth = 1;
        for (uint count = pages; count > 1; depth++)
        {
            count = checked((count + 15u) / 16u);
            nodes = checked(nodes + count);
        }
        uint stride = PageHeap.AllocationSize(checked(144u + _encoder.MaximumVertexBlobBytes + MaximumTriangles * 3u + 3u));
        var sizes = new ClusterGpuBufferSizes(stride, pages, nodes, Math.Max(1, depth - 1));
        return _resources.ReserveGpu(context, sizes, _encoder.VertexEncoding, revision, bounds);
    }

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
        ClusterGpuBufferSizes sizes = allocation.Sizes;
        var uniforms = new BuildUniforms(allocation.PageOffset, sizes.PageBytes, sizes.PageStride, sizes.PageCount,
            geometry.RootBvh, sizes.BvhNodeCount, (uint)_encoding, (uint)(_encoding >> 32), materialIndex);
        GraphBufferSlice parameters = graph.Upload(uniforms, BufferUsages.Constant);
        GraphBufferSrvId positions = graph.CreateBufferSrv(input.Positions);
        GraphBufferSrvId attributes = graph.CreateBufferSrv(input.Attributes);
        GraphBufferSrvId indices = graph.CreateBufferSrv(input.Indices);
        GraphBufferSrvId counts = graph.CreateBufferSrv(input.Counts);
        GraphBufferUavId outputPages = graph.CreateBufferUav(_pages,
            new BufferRange(allocation.PageOffset, sizes.PageBytes));
        GraphBufferUavId outputBvh = graph.CreateBufferUav(_bvh,
            new BufferRange(allocation.Bvh.Offset, allocation.Bvh.Length));
        var bindings = new List<GraphParameterResourceBinding>();
        foreach (ClusterLinkedStageResource resource in _bindings.Resources)
            bindings.Add(resource.Name switch
            {
                "ClusterBuildParameters" => GraphParameterResourceBinding.ConstantBuffer(parameters, PipelineSync.ComputeShading),
                "ClusterBuildPositions" => Read(positions),
                "ClusterBuildAttributes" => Read(attributes),
                "ClusterBuildIndices" => Read(indices),
                "ClusterBuildCounts" => GraphParameterResourceBinding.ReadOnlyBuffer(counts, PipelineSync.ComputeShading),
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
            GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
        static GraphParameterResourceBinding Write(GraphBufferUavId view) =>
            GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete);
    }

    public void Dispose() => _pipeline.Dispose();

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private readonly record struct BuildUniforms(uint PageOffset, uint PageBytes, uint PageStride, uint PageCount,
        uint BvhRoot, uint BvhNodeCount, uint EncodingLow, uint EncodingHigh, uint MaterialIndex);
    private sealed record BuildPass(ClusterComputePipeline Pipeline, GraphParameterResourceBinding[] Bindings);
}
