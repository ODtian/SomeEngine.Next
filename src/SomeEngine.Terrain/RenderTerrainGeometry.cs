using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Terrain;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

internal sealed class RenderTerrainGeometry : ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>,
    IRenderGraphFrameExecutionObserver, IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly RenderTerrainData _fields;
    private readonly TerrainClusterAdapter _adapter;
    private readonly object _scene;
    private readonly ClusterGpuBuilder _builder;
    private readonly GraphExtensionPointId _extensionPoint;
    private readonly Dictionary<TerrainGeometryRequest, Batch> _batches = [];
    private readonly Dictionary<(int Geometry, ulong Revision), ClusterGeometry> _geometries = [];
    private readonly Dictionary<(Shader Shader, string Entry), ComputeData> _pipelines = [];
    private readonly List<Task> _completions = [];
    private QueryHandle _requests, _chunks;

    internal RenderTerrainGeometry(IGraphicsBackend backend, Device device, ClusterRenderResources cluster,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderTerrainData fields, TerrainScene scene)
    {
        _backend = backend;
        _device = device;
        _fields = fields;
        _adapter = scene.GpuAdapter!;
        _scene = scene.RenderKey;
        _builder = new(backend, device, cluster, graph, _adapter.Encoder);
        using RenderGraphEdit edit = graph.BeginEdit();
        _extensionPoint = edit.AddExtensionPoint("Terrain geometry input");
        edit.Commit();
    }

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        _requests = context.World.Query(new QueryDefinitionBuilder().Read<RenderTerrainRequests>());
        _chunks = context.World.Query(new QueryDefinitionBuilder().Read<RenderTerrainChunk>());
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        var requests = new HashSet<TerrainGeometryRequest>();
        context.World.ExecuteQuery(_requests, query =>
        {
            foreach (QueryChunkView chunk in query.Chunks)
            foreach (RenderTerrainRequests value in chunk.Read<RenderTerrainRequests>())
            foreach (TerrainDataRequest request in value.Requests)
                if (request is TerrainGeometryRequest geometry && ReferenceEquals(geometry.Scene, _scene)
                    && !geometry.IsSubmitted) requests.Add(geometry);
        });
        foreach (TerrainGeometryRequest request in requests)
        {
            if (_batches.ContainsKey(request) || !request.Dependency.IsCompleted) continue;
            request.Dependency.Complete();
            foreach (TerrainGpuChunkBuild chunk in request.Chunks)
            {
                Vector3 center = (chunk.Bounds.Min + chunk.Bounds.Max) * .5f;
                var bounds = new Bounds
                {
                    Center = new Vec3 { X = center.X, Y = center.Y, Z = center.Z },
                    Radius = Vector3.Distance(chunk.Bounds.Min, chunk.Bounds.Max) * .5f,
                };
                ClusterGeometry geometry = _builder.Reserve(context, _adapter.Mesher.GetCapacity(chunk.Bounds),
                    request.Revision, bounds);
                _geometries.Add((chunk.GeometryId, request.Revision), geometry);
            }
            _batches.Add(request, new(request));
        }

        var chunks = new List<(Entity Entity, RenderTerrainChunk Chunk)>();
        context.World.ExecuteQuery(_chunks, query =>
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                var values = chunk.Read<RenderTerrainChunk>();
                for (int row = 0; row < values.Length; row++)
                    if (ReferenceEquals(values[row].Scene, _scene)) chunks.Add((chunk.Entities[row], values[row]));
            }
        });
        var visible = new Dictionary<int, ulong>();
        foreach (var value in chunks)
        {
            visible[value.Chunk.GeometryId] = value.Chunk.Revision;
            if (value.Chunk.Revision == 0) continue;
            ClusterGeometry geometry = _geometries[(value.Chunk.GeometryId, value.Chunk.Revision)];
            var binding = new RenderClusterGeometry(geometry, 0);
            if (!context.World.Has<RenderClusterGeometry>(value.Entity)) context.World.Add(value.Entity, binding);
            else if (context.World.Read<RenderClusterGeometry>(value.Entity) != binding) context.World.Replace(value.Entity, binding);
        }
        foreach (TerrainGeometryRequest request in _batches.Keys)
            if (request.Chunks.All(chunk => visible.GetValueOrDefault(chunk.GeometryId) == request.Revision))
                request.Visibility.Complete();
        foreach (var pair in _geometries.ToArray())
            if (visible.TryGetValue(pair.Key.Geometry, out ulong revision) && pair.Key.Revision < revision)
            {
                _builder.Release(context, pair.Value);
                _geometries.Remove(pair.Key);
            }
        foreach (TerrainGeometryRequest request in _batches.Keys.ToArray())
            if (request.IsSubmitted && request.Chunks.All(chunk => !_geometries.ContainsKey((chunk.GeometryId, request.Revision))))
                _batches.Remove(request);
        _completions.RemoveAll(static task => task.IsCompletedSuccessfully);
    }

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        RenderGraphFrame frame = context.Graph;
        using RenderGraphFrameExtension extension = frame.BeginExtension(_extensionPoint);
        foreach (Batch batch in _batches.Values)
        {
            TerrainGeometryRequest request = batch.Request;
            if (request.IsSubmitted || batch.Pass != default || !request.Dependency.IsCompleted) continue;
            request.Dependency.Complete();
            var inputExtension = extension;
            var sourceBindings = new Dictionary<string, GraphParameterResourceBinding>();
            request.Surface!.AddBindings(_fields, ref frame, ref inputExtension, sourceBindings);
            GraphBufferSlice tables = frame.Upload<uint>(RegularCells(), BufferUsages.ShaderRead, "Terrain regular cells");
            GraphPassId previous = default;
            foreach (TerrainGpuChunkBuild chunk in request.Chunks)
            {
                ClusterGpuMeshCapacity capacity = _adapter.Mesher.GetCapacity(chunk.Bounds);
                var parameters = new SurfaceParameters(chunk.Bounds.Min, chunk.Bounds.CellSize, chunk.Bounds.CellCount,
                    capacity.VertexCount, capacity.TriangleCount);
                var bindings = new Dictionary<string, GraphParameterResourceBinding>(sourceBindings)
                {
                    ["TerrainParameters"] = GraphParameterResourceBinding.ConstantBuffer(frame.Upload(parameters, BufferUsages.Constant), PipelineSync.ComputeShading),
                    ["TerrainRegularCells"] = Read(frame.CreateBufferSrv(tables, structureStride: sizeof(uint))),
                };
                GraphBufferSlice positions = CreateBuffer(ref frame, capacity.VertexCount * 12u, "Terrain positions");
                GraphBufferSlice samples = CreateBuffer(ref frame, capacity.VertexCount * 24u, "Terrain sampling points");
                GraphBufferSlice indices = CreateBuffer(ref frame, capacity.TriangleCount * 12u, "Terrain indices");
                GraphBufferSlice counts = CreateBuffer(ref frame, 8, "Terrain geometry counts");
                GraphBufferSlice attributes = CreateBuffer(ref frame, capacity.VertexCount * _adapter.Attributes.VertexBytes, "Terrain vertex attributes");
                bindings["TerrainPositions"] = WritePrefix(frame.CreateBufferUav(positions.Buffer));
                bindings["TerrainSamples"] = WritePrefix(frame.CreateBufferUav(samples.Buffer));
                bindings["TerrainIndices"] = WritePrefix(frame.CreateBufferUav(indices.Buffer));
                bindings["TerrainCounts"] = Write(frame.CreateBufferUav(counts.Buffer));
                var extraction = new TerrainGpuMesherContext(this, frame, chunk.Bounds, bindings);
                _adapter.Mesher.Record(ref extraction);
                bindings["TerrainPositions"] = ReadPrefix(frame.CreateBufferSrv(positions));
                bindings["TerrainSamples"] = ReadPrefix(frame.CreateBufferSrv(samples));
                bindings["TerrainIndices"] = ReadPrefix(frame.CreateBufferSrv(indices));
                bindings["TerrainCounts"] = Read(frame.CreateBufferSrv(counts));
                bindings["TerrainAttributes"] = WritePrefix(frame.CreateBufferUav(attributes.Buffer));
                AddCompute(ref frame, _adapter.Attributes.Shader, _adapter.Attributes.EntryPoint, bindings,
                    new((capacity.VertexCount + 63) / 64, 1, 1));
                ClusterGeometry geometry = _geometries[(chunk.GeometryId, request.Revision)];
                GraphPassId pass = _builder.Record(ref context, geometry, new(positions, attributes, indices, counts),
                    _adapter.Attributes.MaterialIndex);
                if (previous != default) frame.OrderAfter(pass, previous);
                previous = pass;
            }
            batch.Pass = previous;
        }
    }

    internal GraphPassId AddCompute(ref RenderGraphFrame frame, Shader shader, string entry,
        IReadOnlyDictionary<string, GraphParameterResourceBinding> bindings, DispatchArguments dispatch)
    {
        if (!_pipelines.TryGetValue((shader, entry), out ComputeData? compute))
        {
            LinkedComputePipeline pipeline = LinkedComputePipeline.Create(_backend, _device, shader, entry, "Terrain geometry");
            compute = new(pipeline, ClusterLinkedStageLayout.Create(pipeline.ParameterLayout));
            _pipelines.Add((shader, entry), compute);
        }
        var resources = new List<GraphParameterResourceBinding>();
        foreach (ClusterLinkedStageResource resource in compute.Layout.Resources) resources.Add(bindings[resource.Name]);
        var value = new ComputePass(compute.Pipeline, resources.ToArray(), dispatch);
        return frame.AddComputePass(entry, PassQueueSelection.AnyCompatible(), value,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref ComputePass data) =>
            {
                access.SetPipeline(data.Pipeline.Pipeline);
                access.SetParameterBlock(data.Pipeline.ParameterLayout);
                access.Bind(data.Bindings);
            },
            static (ref ComputePassCommandScope commands, in ComputePass data) => commands.Dispatch(data.Dispatch));
    }

    public void OnRenderGraphSubmitted(RenderGraphFrameResult result)
    {
        foreach (Batch batch in _batches.Values)
        {
            TerrainGeometryRequest request = batch.Request;
            if (request.IsSubmitted || !result.TryGetPassCompletion(batch.Pass, out QueueCompletion completion)) continue;
            request.AcceptSubmission();
            _completions.Add(Task.Run(() =>
            {
                try
                {
                    WaitStatus status = _backend.WaitCpu(completion, Timeout.InfiniteTimeSpan);
                    if (status != WaitStatus.Completed) throw new InvalidOperationException($"Terrain geometry ended with {status}.");
                    request.ReleaseInput();
                    request.Completion.Complete();
                }
                catch (Exception failure) { request.Completion.Fail(failure); }
            }));
        }
    }

    public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) => OnRenderGraphSubmitted(result);

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        foreach (ClusterGeometry geometry in _geometries.Values) _builder.Release(context, geometry);
        context.World.ReleaseQuery(_requests);
        context.World.ReleaseQuery(_chunks);
    }

    public void Dispose()
    {
        Task.WhenAll(_completions).GetAwaiter().GetResult();
        foreach (TerrainGeometryRequest request in _batches.Keys)
        {
            request.ReleaseInput();
            if (!request.IsSubmitted) request.Completion.Fail(new OperationCanceledException("Terrain graphics finished."));
            if (!request.Visibility.IsSignaled) request.Visibility.Fail(new OperationCanceledException("Terrain graphics finished."));
        }
        foreach (ComputeData pipeline in _pipelines.Values) pipeline.Pipeline.Dispose();
        _builder.Dispose();
    }

    private static GraphBufferSlice CreateBuffer(ref RenderGraphFrame frame, ulong bytes, string name) =>
        new(frame.CreateBuffer(new BufferDesc(bytes, BufferUsages.ShaderRead | BufferUsages.ShaderWrite, name)), new(0, bytes));
    private static GraphParameterResourceBinding Read(GraphBufferSrvId view) =>
        GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading);
    private static GraphParameterResourceBinding Write(GraphBufferUavId view) =>
        GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
    private static GraphParameterResourceBinding ReadPrefix(GraphBufferSrvId view) =>
        GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
    private static GraphParameterResourceBinding WritePrefix(GraphBufferUavId view) =>
        GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading, GraphAccessMode.Write,
            WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix);

    private static uint[] RegularCells() => [.. TransvoxelTables.RegularClasses.ToArray().Select(static value => (uint)value),
        .. TransvoxelTables.RegularCounts.ToArray().Select(static value => (uint)value),
        .. TransvoxelTables.RegularIndices.ToArray().Select(static value => (uint)value),
        .. TransvoxelTables.RegularEdges.ToArray().Select(static value => (uint)value)];

    private sealed class Batch(TerrainGeometryRequest request)
    {
        internal TerrainGeometryRequest Request { get; } = request;
        internal GraphPassId Pass;
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private readonly record struct SurfaceParameters(Vector3 Min, float CellSize, int CellCount,
        uint VertexCapacity, uint TriangleCapacity);
    private sealed record ComputeData(LinkedComputePipeline Pipeline, ClusterLinkedStageLayout Layout);
    private sealed record ComputePass(LinkedComputePipeline Pipeline, GraphParameterResourceBinding[] Bindings, DispatchArguments Dispatch);
}
