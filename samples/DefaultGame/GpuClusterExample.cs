using System.Numerics;
#if SOMEENGINE_DEVELOPMENT
using SomeEngine.Assets.Importers;
#endif
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using ClusterGeometry = SomeEngine.Render.Cluster.ClusterGeometry;
using SomeEngine.Runtime;
using Shader = SomeEngine.Resources.Shader;

namespace DefaultGame;

/// <summary>GPU-built geometry using explicit float3 positions and float2 user UV attributes.</summary>
public sealed class GpuClusterExample : IApplicationGeometryFeature, IDisposable
{
    private const string Encoding = "example.vertex.float3_uv.v1";
    private readonly Shader _buildShader;
    private readonly Shader _drawShader;
    private Material? _material;
    private ExampleMesh _mesh = new(1,
        [new(-1, -1, 0), new(1, -1, 0), new(-1, 1, 0), new(1, 1, 0)],
        [new(0, 0), new(1, 0), new(0, 1), new(1, 1)], [0, 2, 1, 1, 2, 3],
        new Bounds { Radius = MathF.Sqrt(2) });
    public bool BuildCompleted { get; private set; }
    public ulong GeometryRevision { get; private set; }

    private GpuClusterExample(Shader build, Shader draw) { _buildShader = build; _drawShader = draw; }

#if SOMEENGINE_DEVELOPMENT
    public static async Task<GpuClusterExample> CreateAsync(string contentDirectory)
    {
        string shaders = Path.Combine(contentDirectory, "assets", "Shaders");
        var description = SlangShaderImporter.CreateSessionDescription(
            Path.Combine(shaders, "gpu_cluster_build.slang"),
            SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession), [ProjectPaths.EngineShaders]);
        using var build = new ShaderSourceBlock { SourceNames = ["gpu_cluster_build.slang"] };
        using var draw = new ShaderSourceBlock { SourceNames = ["gpu_cluster_material.slang"] };
        Shader buildShader = await build.CreateAsync(SlangShaderImporter.GlobalSession, description, "GPU Cluster example build");
        Shader drawShader = await draw.CreateAsync(SlangShaderImporter.GlobalSession, description, "GPU Cluster example material");
        return new(buildShader, drawShader);
    }
#endif

    public void SetMaterial(UnlitMaterial material)
    {
        _material = new UnlitMaterial
        {
            Shader = _drawShader,
            AlbedoMap = material.AlbedoMap,
            MaterialSampler = material.MaterialSampler,
            BaseColorTint = material.BaseColorTint,
        };
    }

    public void SetMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> attributes, ReadOnlySpan<uint> indices)
    {
        float radius = 0;
        foreach (Vector3 position in positions) radius = Math.Max(radius, position.Length());
        _mesh = new(_mesh.Revision + 1, positions.ToArray(), attributes.ToArray(), indices.ToArray(), new Bounds { Radius = radius });
        BuildCompleted = false;
    }

    /// <summary>Creates example grid data; the builder accepts arbitrary user mesh buffers.</summary>
    public void SetGrid(int cells, Vector2 size)
    {
        int side = cells + 1;
        var positions = new Vector3[side * side];
        var attributes = new Vector2[positions.Length];
        var indices = new uint[cells * cells * 6];
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            var uv = new Vector2((float)x / cells, (float)y / cells);
            positions[y * side + x] = new((uv.X - .5f) * size.X, (uv.Y - .5f) * size.Y, 0);
            attributes[y * side + x] = uv;
        }
        for (int y = 0; y < cells; y++)
        for (int x = 0; x < cells; x++)
        {
            uint first = (uint)(y * side + x);
            int offset = (y * cells + x) * 6;
            indices[offset] = first;
            indices[offset + 1] = first + (uint)side;
            indices[offset + 2] = first + 1;
            indices[offset + 3] = first + 1;
            indices[offset + 4] = first + (uint)side;
            indices[offset + 5] = first + (uint)side + 1;
        }
        SetMesh(positions, attributes, indices);
    }

    public IDisposable Install(ApplicationGeometryContext context)
    {
        var rendering = new ExampleRendering(context, _buildShader, this);
        context.AddGameSystem(new ExampleGame(this));
        context.AddExtractor(new ExampleExtractor());
        context.AddPrepareSystem(rendering);
        context.AddFrameSystem(rendering);
        context.AddCompletionObserver(rendering);
        return rendering;
    }

    public void Dispose() => _material = null;

    private sealed record ExampleMesh(ulong Revision, Vector3[] Positions, Vector2[] Attributes, uint[] Indices, Bounds Bounds);
    private readonly record struct ExampleGeometry(Material Material, ExampleMesh Mesh) : IComponent;
    private readonly record struct ExampleRenderGeometry(ExampleMesh Mesh) : IComponent;

    private sealed class ExampleGame(GpuClusterExample example) : ISystem<ImmediateSystemContext>
    {
        private Entity _entity;
        private ulong _revision;
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (example._material is null || _revision == example._mesh.Revision) return;
            var value = new ExampleGeometry(example._material, example._mesh);
            if (_entity == default) _entity = context.World.CreateEntity(value);
            else context.World.Replace(_entity, value);
            _revision = example._mesh.Revision;
        }
    }

    private sealed class ExampleExtractor : IRenderExtractionSystem
    {
        private readonly List<(Entity Source, ExampleGeometry Geometry)> _values = [];
        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<ExampleGeometry>();
            query.Write<ExampleRenderGeometry>();
            query.Write<RenderMaterialReference>();
        }
        public void CollectResources(scoped QueryCursor source, RenderExtractionResources resources)
        {
            foreach (QueryChunkView chunk in source.Chunks)
                if (chunk.TryRead<ExampleGeometry>(out var values))
                    foreach (var value in values) resources.Add(value.Material);
        }
        public void Reset() => _values.Clear();
        public void Collect(QueryChunkView chunk)
        {
            if (!chunk.TryRead<ExampleGeometry>(out var geometries)) return;
            for (int index = 0; index < geometries.Length; index++)
                _values.Add((chunk.Entities[index], geometries[index]));
        }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
        {
            if (!chunk.Has<ExampleGeometry>() || !chunk.HasChangedSinceLastSystemVersion<ExampleGeometry>()) return false;
            Collect(chunk);
            return true;
        }
        public void ApplyChanges(RenderExtractionContext context)
        {
            foreach (var value in _values)
                context.UpdateExisting(context.RequireMirror(value.Source), new ExampleRenderGeometry(value.Geometry.Mesh));
        }
        public void Apply(RenderExtractionContext context)
        {
            foreach (var value in _values)
            {
                Entity entity = context.RetainMirror(value.Source);
                context.Upsert(entity, new ExampleRenderGeometry(value.Geometry.Mesh));
                context.Upsert(entity, new RenderInstance());
                // This placement and the quad dimensions below are example scene data.
                context.RetainTransform(entity, new RenderTransform(new TransformQvvs(new Vector3(0, 0, 6), Quaternion.Identity)));
                context.SetMaterials(entity, [value.Geometry.Material]);
            }
        }
    }

    private sealed class ExampleRendering : ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>,
        IRenderGraphFrameExecutionObserver, IDisposable
    {
        private readonly ClusterGpuBuilder _builder;
        private readonly IGraphicsBackend _backend;
        private readonly GpuClusterExample _example;
        private QueryHandle _query;
        private ClusterGeometry _geometry;
        private ExampleBuild? _build;
        private bool _reserved;

        private sealed class ExampleBuild(ClusterGeometry geometry, ExampleMesh mesh)
        {
            internal ClusterGeometry Geometry = geometry;
            internal ExampleMesh Mesh = mesh;
            internal GraphPassId Pass;
            internal bool Complete;
        }

        internal ExampleRendering(ApplicationGeometryContext context, Shader shader, GpuClusterExample example)
        {
            _backend = context.Backend;
            _example = example;
            _builder = new(context.Backend, context.Device, context.Cluster, context.Graph,
                new(shader, "BuildExampleCluster", Encoding, 64 * 20));
        }
        public void OnCreate(ref RenderPrepareSystemContext context) =>
            _query = context.World.Query(new QueryDefinitionBuilder().Read<ExampleRenderGeometry>());
        public void OnDestroy(ref RenderPrepareSystemContext context)
        {
            if (_reserved) _builder.Release(context, _geometry);
            if (_build is { } build && build.Geometry != _geometry) _builder.Release(context, build.Geometry);
            context.World.ReleaseQuery(_query);
        }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            Entity entity = default;
            ExampleMesh? mesh = null;
            context.World.ExecuteQuery(_query, query =>
            {
                foreach (QueryChunkView chunk in query.Chunks)
                {
                    entity = chunk.Entities[0];
                    mesh = chunk.Read<ExampleRenderGeometry>()[0].Mesh;
                }
            });
            if (entity == default) return;
            if (_build is { Complete: true } completed)
            {
                if (completed.Geometry != _geometry)
                {
                    context.World.Replace(entity, new RenderClusterGeometry(completed.Geometry, 0));
                    _builder.Release(context, _geometry);
                    _geometry = completed.Geometry;
                    _example.GeometryRevision = _geometry.Revision;
                }
                _build = null;
            }
            if (_build is not null || _reserved && _geometry.Revision == mesh!.Revision) return;
            var capacity = new ClusterGpuMeshCapacity((uint)mesh!.Positions.Length, (uint)mesh.Indices.Length / 3);
            ClusterGeometry geometry = _builder.Reserve(context, capacity, mesh.Revision, mesh.Bounds);
            _build = new(geometry, mesh);
            if (!_reserved)
            {
                _geometry = geometry;
                context.World.Add(entity, new RenderClusterGeometry(_geometry, 0));
                _example.GeometryRevision = _geometry.Revision;
                _reserved = true;
            }
        }
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            if (_build is not { } build || build.Pass != default) return;
            RenderGraphFrame graph = context.Graph;
            ExampleMesh mesh = build.Mesh;
            uint[] counts = [(uint)mesh.Positions.Length, (uint)mesh.Indices.Length];
            var input = new ClusterGpuMeshInput(
                graph.Upload<Vector3>(mesh.Positions, BufferUsages.ShaderRead),
                graph.Upload<Vector2>(mesh.Attributes, BufferUsages.ShaderRead),
                graph.Upload<uint>(mesh.Indices, BufferUsages.ShaderRead),
                graph.Upload<uint>(counts, BufferUsages.ShaderRead));
            build.Pass = _builder.Record(ref context, build.Geometry, input);
        }
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            if (_build is not { Complete: false } build || !result.TryGetPassCompletion(build.Pass, out QueueCompletion completion)) return;
            if (_backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed) throw new TimeoutException();
            build.Complete = _backend.IsComplete(completion);
            _example.BuildCompleted = build.Complete;
        }
        public void Dispose() => _builder.Dispose();
    }
}
