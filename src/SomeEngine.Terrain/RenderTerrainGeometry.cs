using SomeEngine.Render;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Terrain;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

internal sealed class RenderTerrainGeometry : ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>,
    IDisposable
{

    private readonly RenderPipelines _renderPipelines;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly RenderTerrainData _fields;
    private readonly TerrainClusterAdapter _adapter;
    private readonly object _scene;
    private readonly ClusterGpuBuilder _builder;
    private readonly GraphRegionId _extensionPoint;
    private readonly Dictionary<TerrainGeometryRequest, Batch> _batches = [];
    private readonly Dictionary<(int Geometry, ulong Revision), GeometryData> _geometries = [];
    private readonly Dictionary<(Shader Shader, string Entry), ComputeData> _pipelines = [];
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private QueryHandle _requests, _chunks, _selection;

    internal RenderTerrainGeometry(IGraphicsBackend backend, Device device, RenderPipelines pipelines, ClusterRenderResources cluster,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderTerrainData fields, TerrainScene scene)
    {

        _renderPipelines = pipelines;
        _graph = graph;
        _backend = backend;
        _device = device;
        _fields = fields;
        _adapter = scene.GpuAdapter!;
        _scene = scene.RenderKey;
        foreach (TerrainShaderEntry entry in _adapter.Mesher.Entries) AddPipeline(entry.Shader, entry.EntryPoint);
        AddPipeline(_adapter.Attributes.Shader, _adapter.Attributes.EntryPoint);
        if (_adapter.Attributes.MaterialEntryPoint is { } materialEntry) AddPipeline(_adapter.Attributes.Shader, materialEntry);
        _builder = new(backend, device, pipelines, cluster, graph, _adapter.Encoder);
        using RenderGraphEdit edit = graph.BeginEdit();
        _extensionPoint = edit.AddFrameRegion("Terrain geometry input");
        edit.Commit();
    }

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        _requests = context.World.Query(new QueryDefinitionBuilder().Read<RenderTerrainRequests>());
        _chunks = context.World.Query(new QueryDefinitionBuilder().Read<RenderTerrainChunk>().ReadBuffer<RenderMaterialBinding>());
        _selection = context.World.Query(new QueryDefinitionBuilder().Read<RenderTerrainSelection>());
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        var selected = new HashSet<int>();
        context.World.ExecuteQuery(_selection, query =>
        {
            foreach (QueryChunkView chunk in query.Chunks)
            foreach (RenderTerrainSelection value in chunk.Read<RenderTerrainSelection>())
                if (ReferenceEquals(value.Scene, _scene)) selected.UnionWith(value.GeometryIds);
        });
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
            _batches.TryAdd(request, new(request));
        foreach (Batch batch in _batches.Values)
        {
            TerrainGeometryRequest request = batch.Request;
            if (!request.Chunks.Any(chunk => selected.Contains(chunk.GeometryId)))
            {
                request.Withdraw();
                continue;
            }
            if (batch.Reserved || request.IsSubmitted || !request.Dependency.IsCompleted) continue;
            request.Dependency.Complete();
            foreach (TerrainGpuChunkBuild chunk in request.Chunks)
            {
                Vector3 center = (chunk.Bounds.Min + chunk.Bounds.Max) * .5f;
                var bounds = new Bounds
                {
                    Center = new Vec3 { X = center.X, Y = center.Y, Z = center.Z },
                    Radius = Vector3.Distance(chunk.Bounds.Min, chunk.Bounds.Max) * .5f,
                };
                ClusterGeometry geometry = _builder.Reserve(context, GetCapacity(chunk.Bounds),
                    request.Revision, bounds);
                _geometries.Add((chunk.GeometryId, request.Revision), new(geometry));
            }
            batch.Reserved = true;
        }

        var world = context.World;
        var materials = new List<long>();
        var chunks = new List<(Entity Entity, RenderTerrainChunk Chunk, int FirstMaterial, int MaterialCount)>();
        context.World.ExecuteQuery(_chunks, query =>
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                var values = chunk.Read<RenderTerrainChunk>();
                for (int row = 0; row < values.Length; row++)
                {
                    if (!ReferenceEquals(values[row].Scene, _scene)) continue;
                    ReadOnlySpan<RenderMaterialBinding> bindings = chunk.ReadBuffer<RenderMaterialBinding>(row).AsSpan();
                    int first = materials.Count;
                    foreach (RenderMaterialBinding binding in bindings) materials.Add(binding.MaterialId);
                    chunks.Add((chunk.Entities[row], values[row], first, bindings.Length));
                }
            }
        });
        var visible = new Dictionary<int, ulong>();
        foreach (var value in chunks)
        {
            visible[value.Chunk.GeometryId] = value.Chunk.Revision;
            if (value.Chunk.Revision == 0) continue;
            GeometryData geometry = _geometries[(value.Chunk.GeometryId, value.Chunk.Revision)];
            geometry.HasBeenBound = true;
            float expansion = 0f;
            for (int material = value.FirstMaterial; material < value.FirstMaterial + value.MaterialCount; material++)
                expansion = Math.Max(expansion, world.ReadMaterial(materials[material]).BoundsExpansion);
            var binding = new RenderClusterGeometry(geometry.Geometry, expansion);
            if (!context.World.Has<RenderClusterGeometry>(value.Entity)) context.World.Add(value.Entity, binding);
            else if (context.World.Read<RenderClusterGeometry>(value.Entity) != binding) context.World.Replace(value.Entity, binding);
        }
        foreach (TerrainGeometryRequest request in _batches.Keys)
            if (request.Chunks.All(chunk => !selected.Contains(chunk.GeometryId)
                || visible.GetValueOrDefault(chunk.GeometryId) == request.Revision))
                request.Visibility.Complete();
        foreach (TerrainGeometryRequest request in _batches.Keys)
        foreach (TerrainGpuChunkBuild chunk in request.Chunks)
            if (_geometries.TryGetValue((chunk.GeometryId, request.Revision), out GeometryData? geometry)
                && ((geometry.HasBeenBound && visible.GetValueOrDefault(chunk.GeometryId) != request.Revision)
                    || (request.Visibility.IsSignaled && !selected.Contains(chunk.GeometryId))))
            {
                _builder.Release(context, geometry.Geometry);
                _geometries.Remove((chunk.GeometryId, request.Revision));
            }
        foreach (TerrainGeometryRequest request in _batches.Keys.ToArray())
            if (request.IsSubmitted && request.Chunks.All(chunk => !_geometries.ContainsKey((chunk.GeometryId, request.Revision))))
                _batches.Remove(request);
    }

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        RenderGraphFrame frame = context.Graph;
        var extension = new TerrainFrameCommands(frame, _extensionPoint);
        GeometrySubmission submission = frame.GetFrameData(this, () => new GeometrySubmission(this));
        frame.ObserveExecution(submission);
        foreach (Batch batch in _batches.Values)
        {
            TerrainGeometryRequest request = batch.Request;
            if (request.IsSubmitted || !batch.Reserved || !submission.Requests.Add(request)) continue;
            request.Dependency.Complete();
            var inputExtension = extension;
            var sourceBindings = new Dictionary<string, GraphParameterResourceBinding>();
            request.Surface!.AddBindings(_fields, ref frame, ref inputExtension, sourceBindings);
            GraphBufferSlice tables = frame.Upload<uint>(RegularCells(), BufferUsages.ShaderRead, "Terrain regular cells");
            GraphBufferSlice transitions = frame.Upload<uint>(TransitionCells(), BufferUsages.ShaderRead, "Terrain transition cells");
            GraphPassId previous = default;
            foreach (TerrainGpuChunkBuild chunk in request.Chunks)
            {
                ClusterGpuMeshCapacity capacity = GetCapacity(chunk.Bounds);
                var parameters = new SurfaceParameters(chunk.Bounds.Min, chunk.Bounds.CellSize, chunk.Bounds.CellCount,
                    capacity.VertexCount, capacity.TriangleCount, chunk.Bounds.FinestCellSize, (uint)chunk.Bounds.TransitionFaces);
                var bindings = new Dictionary<string, GraphParameterResourceBinding>(sourceBindings)
                {
                    ["TerrainParameters"] = GraphParameterResourceBinding.ConstantBuffer(frame.Upload(parameters, BufferUsages.Constant), PipelineSync.ComputeShading),
                    ["TerrainRegularCells"] = Read(frame.CreateBufferSrv(tables, structureStride: sizeof(uint))),
                    ["TerrainTransitionCells"] = Read(frame.CreateBufferSrv(transitions, structureStride: sizeof(uint))),
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
                GraphBufferSlice materials = default;
                if (_adapter.Attributes.MaterialEntryPoint is not null)
                {
                    materials = CreateBuffer(ref frame, capacity.TriangleCount * sizeof(uint), "Terrain triangle materials");
                    bindings["TerrainTriangleMaterials"] = WritePrefix(frame.CreateBufferUav(materials.Buffer));
                }
                AddCompute(ref frame, _adapter.Attributes.Shader, _adapter.Attributes.EntryPoint, bindings,
                    new((capacity.VertexCount + 63) / 64, 1, 1));
                if (_adapter.Attributes.MaterialEntryPoint is { } materialEntry)
                {
                    AddCompute(ref frame, _adapter.Attributes.Shader, materialEntry, bindings,
                        new((capacity.TriangleCount + 63) / 64, 1, 1));
                }
                ClusterGeometry geometry = _geometries[(chunk.GeometryId, request.Revision)].Geometry;
                GraphPassId pass = _builder.Record(ref context, geometry, new(positions, attributes, indices, counts, materials),
                    _adapter.Attributes.MaterialIndex);
                if (previous != default) frame.OrderAfter(pass, previous);
                previous = pass;
            }
        }
    }

    private ClusterGpuMeshCapacity GetCapacity(TerrainChunkBounds bounds) =>
        _adapter.Mesher.GetCapacity(bounds) with { MaterialCount = _adapter.Attributes.MaterialCount };

    internal GraphPassId AddCompute(ref RenderGraphFrame frame, Shader shader, string entry,
        IReadOnlyDictionary<string, GraphParameterResourceBinding> bindings, DispatchArguments dispatch)
    {
        ComputeData compute = _pipelines[(shader, entry)];
        var resources = new List<GraphParameterResourceBinding>();
        foreach (ShaderParameterResource resource in compute.Layout.Resources) resources.Add(bindings[resource.Name]);
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

    private void AddPipeline(Shader shader, string entry)
    {
        if (_pipelines.ContainsKey((shader, entry))) return;
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(_renderPipelines, shader, entry, "Terrain geometry");
        _pipelines.Add((shader, entry), new(pipeline, ShaderParameterResources.Create(pipeline.ParameterLayout)));
    }

    private sealed class GeometrySubmission(RenderTerrainGeometry geometry) : IRenderGraphFrameExecutionObserver
    {
        internal readonly HashSet<TerrainGeometryRequest> Requests = [];
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            foreach (TerrainGeometryRequest request in Requests)
            {
                request.AcceptSubmission();
                geometry._graph.RetireAfterSubmittedFrames(new CompletedGeometry(request));
            }
        }
    }

    private sealed class CompletedGeometry(TerrainGeometryRequest request) : IDisposable
    {
        public void Dispose()
        {
            request.ReleaseInput();
            request.Completion.Complete();
        }
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        foreach (TerrainGeometryRequest request in _batches.Keys) request.Withdraw();
        foreach (GeometryData geometry in _geometries.Values) _builder.Release(context, geometry.Geometry);
        context.World.ReleaseQuery(_requests);
        context.World.ReleaseQuery(_chunks);
        context.World.ReleaseQuery(_selection);
    }

    public void Dispose()
    {
        foreach (TerrainGeometryRequest request in _batches.Keys)
        {
            request.ReleaseInput();
            request.Withdraw();
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

    private static uint[] TransitionCells() => [.. TransvoxelTables.TransitionClasses.ToArray().Select(static value => (uint)value),
        .. TransvoxelTables.TransitionCounts.ToArray().Select(static value => (uint)value),
        .. TransvoxelTables.TransitionIndices.ToArray().Select(static value => (uint)value),
        .. TransvoxelTables.TransitionEdges.ToArray().Select(static value => (uint)value)];

    private sealed class GeometryData(ClusterGeometry geometry)
    {
        internal ClusterGeometry Geometry { get; } = geometry;
        internal bool HasBeenBound;
    }

    private sealed class Batch(TerrainGeometryRequest request)
    {
        internal TerrainGeometryRequest Request { get; } = request;
        internal bool Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private readonly record struct SurfaceParameters(Vector3 Min, float CellSize, int CellCount,
        uint VertexCapacity, uint TriangleCapacity, float FinestCellSize, uint TransitionFaces);
    private sealed record ComputeData(LinkedComputePipeline Pipeline, ShaderParameterResources Layout);
    private sealed record ComputePass(LinkedComputePipeline Pipeline, GraphParameterResourceBinding[] Bindings, DispatchArguments Dispatch);
}
