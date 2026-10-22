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
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--static-scene", "--no-debug-ui",
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
            context.AddCompletionObserver(transfer);
            return transfer;
        }
    }

    private sealed class GeometryTransfer : ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>,
        IRenderGraphFrameExecutionObserver, IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private readonly List<uint> _values;
        private readonly Buffer _output;
        private readonly GraphBufferInput _input;
        private readonly GraphPassId _copy;
        private QueryHandle _query;
        private uint _value;
        private QueueCompletion _lastCompletion;

        public GeometryTransfer(ApplicationGeometryContext context, List<uint> values)
        {
            _backend = context.Backend;
            _values = values;
            _output = _backend.CreateBuffer(context.Device, new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
            using RenderGraphEdit edit = context.Graph.BeginEdit();
            _input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId destination = edit.RegisterExternalBuffer(_output,
                [new(BufferRange.Whole, _output.InitialSync, _output.InitialAccess, ResourceContentState.Undefined)]);
            _copy = edit.AddCopyFramePass("geometry sample", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Source: _input, Destination: destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref (GraphBufferInput Source, GraphBufferId Destination) data) =>
                {
                    access.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    access.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Source, GraphBufferId Destination) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Source);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Destination), 0, 4));
                });
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

        public void OnRenderGraphSubmitted(RenderGraphFrameResult result)
        {
            if (!result.TryGetPassCompletion(_copy, out QueueCompletion completion) || completion == _lastCompletion) return;
            _lastCompletion = completion;
            if (_backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed) throw new TimeoutException();
            using MappedBuffer mapping = _backend.Map(_output, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            _values.Add(BitConverter.ToUInt32(mapping.Bytes));
        }

        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) { }
        public void Dispose() => _output.Dispose();
    }
}
