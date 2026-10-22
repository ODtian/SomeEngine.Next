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
using SomeEngine.Runtime;
using Shader = SomeEngine.Resources.Shader;

namespace DefaultGame;

/// <summary>A GPU-built quad using explicit float3 positions and float2 user UV attributes.</summary>
public sealed class GpuClusterExample : IApplicationGeometryFeature, IDisposable
{
    private const string Encoding = "example.vertex.float3_uv.v1";
    private readonly Shader _buildShader;
    private readonly Shader _drawShader;
    private Material? _material;
    public bool BuildCompleted { get; private set; }

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

    public void Dispose() => _material?.Dispose();

    private readonly record struct ExampleGeometry(Material Material) : IComponent;
    private readonly record struct ExampleRenderGeometry : IComponent;

    private sealed class ExampleGame(GpuClusterExample example) : ISystem<ImmediateSystemContext>
    {
        private bool _added;
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (_added || example._material is null) return;
            context.World.CreateEntity(new ExampleGeometry(example._material));
            _added = true;
        }
    }

    private sealed class ExampleExtractor : IRenderExtractionSystem
    {
        private readonly List<(Entity Source, Material Material)> _values = [];
        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<ExampleGeometry>();
            query.Write<ExampleRenderGeometry>();
            query.Write<RenderMaterialReference>();
        }
        public void Reset() => _values.Clear();
        public void Collect(QueryChunkView chunk)
        {
            if (!chunk.TryRead<ExampleGeometry>(out var geometries)) return;
            for (int index = 0; index < geometries.Length; index++)
                _values.Add((chunk.Entities[index], geometries[index].Material));
        }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) => false;
        public void ApplyChanges(RenderExtractionContext context) { }
        public void Apply(RenderExtractionContext context)
        {
            context.UpdateMaterials(_values.Select(value => value.Material), []);
            foreach (var value in _values)
            {
                Entity entity = context.RetainMirror(value.Source);
                context.Upsert(entity, new ExampleRenderGeometry());
                context.Upsert(entity, new RenderInstance());
                // This placement and the quad dimensions below are example scene data.
                context.RetainTransform(entity, new RenderTransform(new TransformQvvs(new Vector3(0, 0, 6), Quaternion.Identity)));
                if (!context.World.HasBuffer<RenderMaterialBinding>(entity)) context.World.AddBuffer<RenderMaterialBinding>(entity);
                long material = value.Material.MaterialId;
                context.World.ExecuteBufferWrite<RenderMaterialBinding, long>(entity, ref material,
                    static (DynamicBuffer<RenderMaterialBinding> buffer, ref long id) =>
                    { buffer.Clear(); buffer.Add(new(id)); });
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
        private GraphPassId _build;
        private bool _reserved;
        private bool _recorded;

        internal ExampleRendering(ApplicationGeometryContext context, Shader shader, GpuClusterExample example)
        {
            _backend = context.Backend;
            _example = example;
            _builder = new(context.Backend, context.Device, context.Cluster,
                new(shader, "BuildExampleCluster", Encoding, 64 * 20));
        }
        public void OnCreate(ref RenderPrepareSystemContext context) =>
            _query = context.World.Query(new QueryDefinitionBuilder().Read<ExampleRenderGeometry>());
        public void OnDestroy(ref RenderPrepareSystemContext context)
        {
            if (_reserved) _builder.Release(context, _geometry);
            context.World.ReleaseQuery(_query);
        }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            if (_reserved) return;
            Entity entity = default;
            context.World.ExecuteQuery(_query, query =>
            { foreach (QueryChunkView chunk in query.Chunks) entity = chunk.Entities[0]; });
            if (entity == default) return;
            _geometry = _builder.Reserve(context, 1, new Bounds { Radius = MathF.Sqrt(2) });
            context.World.Add(entity, new RenderClusterGeometry(_geometry, 0));
            _reserved = true;
        }
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            if (!_reserved || _recorded) return;
            RenderGraphFrame graph = context.Graph;
            Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(-1, 1, 0), new(1, 1, 0)];
            Vector2[] attributes = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            uint[] indices = [0, 2, 1, 1, 2, 3];
            uint[] counts = [4, 6];
            var input = new ClusterGpuMeshInput(
                graph.Upload<Vector3>(positions, BufferUsages.ShaderRead),
                graph.Upload<Vector2>(attributes, BufferUsages.ShaderRead),
                graph.Upload<uint>(indices, BufferUsages.ShaderRead),
                graph.Upload<uint>(counts, BufferUsages.ShaderRead));
            _build = _builder.Record(ref context, _geometry, input);
            _recorded = true;
        }
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            if (_example.BuildCompleted || !result.TryGetPassCompletion(_build, out QueueCompletion completion)) return;
            if (_backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed) throw new TimeoutException();
            _example.BuildCompleted = _backend.IsComplete(completion);
        }
        public void Dispose() => _builder.Dispose();
    }
}
