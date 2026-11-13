using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Instances;

/// <summary>Instance data for one RenderWorld and its graph.</summary>
public sealed class RenderInstanceStorageSystem : IDisposable
{
    private readonly RenderInstanceData _storage;

    public RenderInstanceStorageSystem(global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld world,
        RenderInstancePropertyLayout layout, RenderInstanceOptions? options = null)
    {
        World = world;
        _storage = new(graph, layout, options);
    }

    public RenderWorld World { get; }
    public RenderInstancePropertyLayout Layout => _storage.Layout;
    public RenderInstanceOptions Options => _storage.Options;
    public int PropertyDataBytes => _storage.PropertyDataBytes;
    public GraphBufferId PropertyData => _storage.PropertyData;
    public GraphBufferId Metadata => _storage.Metadata;
    internal RenderInstanceData Storage => _storage;

    internal RenderInstanceWriteHandle AllocateBatch(RenderInstancePropertyLayout layout, int instanceCount) =>
        new(new RenderInstanceBatchComposition(_storage, _storage.AllocateBatch(layout, instanceCount)));

    internal RenderInstanceWriteHandle RewriteBatch(RenderInstanceBatch batch, RenderInstancePropertyLayout properties,
        RenderInstancePropertyLayout? replacedProperties) => new(new RenderInstanceBatchComposition(_storage,
            _storage.BeginBatchUpdate(batch, properties, replacedProperties), batch, properties, replacedProperties));

    internal void Retire(RenderInstanceBatch batch) => _storage.ReleaseBatch(batch);
    internal RenderInstanceStorageView OpenRead() => _storage.OpenRead();
    internal void FinishWrites() => _storage.FinishWrites();
    internal void Upload(ref RenderGraphFrame frame) => _storage.Upload(ref frame);
    public RenderInstanceDiagnostics CaptureDiagnostics() => _storage.CaptureDiagnostics();
    public void Dispose() => _storage.Dispose();
}
