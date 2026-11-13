using SomeEngine.Resources;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster;

public sealed record ClusterRenderOptions
{
    public ClusterResidencyOptions Residency { get; init; } = new();
}

public readonly record struct ClusterPrepareResult(bool PublishedResidencyChanges);
public sealed record ClusterRenderDiagnostics(ClusterResidencyDiagnostics Residency, ClusterMeshCacheDiagnostics Meshes);

/// <summary>Geometry resources and instance ranges consumed by one cluster pipeline.</summary>
internal readonly record struct ClusterRenderBinding(
    ClusterResidencyBinding Residency, RenderInstanceBatchView InstanceData, ClusterEpochId ReadbackEpoch)
{
    internal ClusterResidentGeometry? Changes => Residency.Changes;
    internal GraphBufferId PropertyData => InstanceData.PropertyData;
    internal GraphBufferId InstancePropertyMetadata => InstanceData.Metadata;
    internal SomeEngine.Graphics.BufferRange InstancePropertyMetadataRange => InstanceData.MetadataRange;
    internal int DispatchExtent => InstanceData.InstanceCount;
    internal int PageFaultCapacity => Residency.PageFaultCapacity;
    internal SomeEngine.Graphics.BufferDesc PageHeapDescription => Residency.PageHeapDescription;
    internal SomeEngine.Graphics.BufferDesc BvhDescription => Residency.BvhDescription;
    internal SomeEngine.Graphics.BufferRange[] ResidentPageRanges => Residency.ResidentPageRanges;
    internal SomeEngine.Graphics.BufferRange[] BvhRanges => Residency.BvhRanges;
}

/// <summary>Mesh registration, page streaming and geometry data for one rendering world.</summary>
public sealed partial class ClusterRenderResources : IDisposable
{
    private readonly ClusterResidency _residency;
    private readonly RenderInstanceData _instances;
    private readonly RenderWorld _renderWorld;
    private readonly ClusterMeshPrepareSystem _meshPrepare;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private ulong _lastFaultFeedbackFrame;
    private bool _disposed;

    public ClusterRenderResources(global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld renderWorld,
        RenderInstanceStorageSystem instances, ClusterRenderOptions? options = null)
    {
        _graph = graph; _renderWorld = renderWorld; _instances = instances.Storage;
        _residency = new(graph, (options ?? new ClusterRenderOptions()).Residency);
        try { _meshPrepare = new(renderWorld, _residency); }
        catch { _residency.Dispose(); throw; }
    }

    internal ClusterResidency Residency => _residency;
    internal ClusterEpochId EpochId => _residency.EpochId;
    internal uint GeometryPageCount => _residency.GeometryPageCount;
    internal uint MaximumRasterBatchesPerCluster => _residency.MaximumRasterBatchesPerCluster;
    public ReadOnlyMemory<ClusterLodChange> LodChanges => _residency.LodChanges;
    internal RenderWorld World => _renderWorld;
    internal RenderInstanceData InstanceResources => _instances;
    internal int PublishedMeshCount => _residency.PublishedMeshCount;
    internal int HierarchyLevels => _residency.HierarchyLevels;
    internal int PageFaultCapacity => _residency.PageFaultCapacity;

    public ValueTask<ClusterMeshPrepareResult> PrepareMeshesAsync(CancellationToken cancellationToken = default) =>
        _meshPrepare.PrepareAsync(cancellationToken);

    internal ClusterPrepareResult Prepare()
    {
        bool changed = _residency.PrepareChanges();
        _meshPrepare.ApplyGeometry();
        return new(changed);
    }

    internal void CommitResidencyChanges(ClusterResidentGeometry changes, ulong frameIdentity) =>
        _residency.CommitChanges(changes, frameIdentity);

    internal ClusterGeometryProducer CreateGeometryProducer() => new();

    internal ClusterRenderBinding Use(RenderInstanceBatch batch, RenderInstancePropertyLayout shaderLayout) =>
        new(_residency.GetBinding(), _instances.GetBatchView(batch, shaderLayout), EpochId);

    internal void IngestPageFaultReadbackBatch(ClusterReadbackTicket ticket, ReadOnlySpan<byte[]> payloads)
    {
        foreach (byte[] bytes in payloads) _residency.IngestFaults(bytes);
        _lastFaultFeedbackFrame = ticket.FeedbackSequence;
    }

    internal void IngestStreamingFeedback(ClusterReadbackTicket ticket, ReadOnlySpan<byte[]> payloads,
        ReadOnlyMemory<ClusterPagePriority> demands, bool complete, ulong frameIdentity)
    {
        foreach (byte[] bytes in payloads) _residency.IngestFaults(bytes);
        _residency.IngestDemand(demands, complete, frameIdentity);
        _lastFaultFeedbackFrame = ticket.FeedbackSequence;
    }

    public void PumpStreaming() => _residency.PumpStreaming();

    public ClusterRenderDiagnostics CaptureDiagnostics()
    {
        _residency.CaptureDiagnostics(out ClusterResidencyDiagnostics residency, out ClusterMeshCacheDiagnostics meshes);
        return new(residency, meshes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _meshPrepare.Dispose();
        _residency.Dispose();
        _disposed = true;
    }
}
