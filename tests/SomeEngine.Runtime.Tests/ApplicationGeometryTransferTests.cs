using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationGeometryTransferTests(SceneContentFixture content)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeometryFeatureTransfersEachGameUpdateThroughExtractionAndGpuCompletion(bool synchronous)
    {
        var feature = new GeometryTransferFeature();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = ApplicationOptions.Parse(["--hidden-window", "--static-scene", "--no-debug-ui",
                "--no-vsync", "--no-taa", "--no-vsm", "--frames", "2"]) with { SynchronousFrames = synchronous },
        };
        scene.InstallGeometry(feature);

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.Equal([14u, 22u], feature.ReadValues);
    }

    private readonly record struct GeometrySample(uint Value) : IComponent;
    private readonly record struct RenderGeometrySample(uint Value) : IComponent;

    private sealed class GeometrySampleSystem : ISystem<ImmediateSystemContext>
    {
        private Entity _entity;
        public void OnCreate(ref ImmediateSystemContext context) => _entity = context.World.CreateEntity(new GeometrySample(3));
        public void OnUpdate(ref ImmediateSystemContext context) =>
            context.World.Replace(_entity, new GeometrySample(context.World.Read<GeometrySample>(_entity).Value + 4));
    }

    private sealed class GeometrySampleExtractor : IRenderExtractionSystem
    {
        private Entity _source;
        private RenderGeometrySample _sample;
        private bool _collected;

        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<GeometrySample>();
            query.Write<RenderGeometrySample>();
        }

        public void Reset() => _collected = false;

        public void Collect(QueryChunkView chunk)
        {
            if (!chunk.TryRead<GeometrySample>(out var values)) return;
            _source = chunk.Entities[0];
            _sample = new(values[0].Value);
            _collected = true;
        }

        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
        {
            if (!chunk.Has<GeometrySample>() || !chunk.HasChangedSinceLastSystemVersion<GeometrySample>()) return false;
            Collect(chunk);
            return true;
        }

        public void Apply(RenderExtractionContext context)
        {
            if (_collected) context.Upsert(context.RetainMirror(_source), _sample);
        }

        public void ApplyChanges(RenderExtractionContext context)
        {
            if (_collected) context.UpdateExisting(context.RequireMirror(_source), _sample);
        }
    }

    private sealed class GeometryTransferFeature : IApplicationGeometryFeature
    {
        public List<uint> ReadValues { get; } = [];

        public IDisposable Install(ApplicationGeometryContext context)
        {
            var transfer = new GeometryTransfer(context, ReadValues);
            context.AddGameSystem(new GeometrySampleSystem());
            context.AddExtractor(new GeometrySampleExtractor());
            context.AddPrepareSystem(transfer);
            context.AddFrameSystem(transfer);
            return transfer;
        }
    }

    private sealed class GeometryTransfer : ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>,
        IGraphReadbackReceiver, IDisposable
    {
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly List<uint> _values;
        private readonly GraphBufferInput _input;
        private readonly GraphReadbackId _readback;
        private QueryHandle _query;
        private uint _value;

        public GeometryTransfer(ApplicationGeometryContext context, List<uint> values)
        {
            _graph = context.Graph;
            _values = values;
            using RenderGraphEdit edit = _graph.BeginEdit();
            _input = edit.CreateBufferInput(BufferUsages.CopySource);
            _readback = edit.AddReadback(_input, new BufferRange(0, 4), this, "geometry sample");
            edit.Commit();
        }

        public void OnCreate(ref RenderPrepareSystemContext context) =>
            _query = context.World.Query(new QueryDefinitionBuilder().Read<RenderGeometrySample>());

        public void OnUpdate(ref RenderPrepareSystemContext context) =>
            context.World.ExecuteQuery(_query, query =>
            {
                foreach (QueryChunkView chunk in query.Chunks) _value = chunk.Read<RenderGeometrySample>()[0].Value * 2;
            });

        public void OnDestroy(ref RenderPrepareSystemContext context) => context.World.ReleaseQuery(_query);

        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            RenderGraphFrame graph = context.Graph;
            graph.SetBufferInput(_input, graph.Upload(BitConverter.GetBytes(_value), BufferUsages.CopySource));
        }

        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => _values.Add(BitConverter.ToUInt32(bytes));

        public void Dispose()
        {
            if (_graph.IsDisposed || _graph.IsLost) return;
            using RenderGraphEdit edit = _graph.BeginEdit();
            edit.Remove(_readback);
            edit.Commit();
        }

    }
}
