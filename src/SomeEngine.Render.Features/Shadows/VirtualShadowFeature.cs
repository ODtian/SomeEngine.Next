using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>
/// Independent virtual-shadow feature. It owns VSM address-space, cache, atlas, allocation and
/// page-processing pipelines, constructs caster views, then calls Cluster only through its unified
/// feature-neutral multi-view geometry boundary.
/// </summary>
public sealed class VirtualShadowFeature : IDisposable
{
    private readonly VirtualShadowMap _shadowMap;
    private readonly ClusterPipelineSystem _geometry;
    private readonly RenderView[] _unifiedViews;
    private bool _shadowFramePending;
    private bool _framePending;
    private bool _disposed;

    public VirtualShadowFeature(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets,
        VirtualShadowMapShaders shaders,
        VirtualShadowMapSettings settings,
        ClusterPipelineSystem geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        settings.Validate();
        ClusterPipelineOutputs outputs = geometry.Outputs;
        uint requiredViews = checked(1u + settings.MaxShadowViews);
        if (requiredViews > outputs.ViewCapacity)
        {
            throw new ArgumentException(
                $"VSM requires {requiredViews} unified views, but Cluster capacity is {outputs.ViewCapacity}.",
                nameof(settings));
        }
        _shadowMap = new VirtualShadowMap(backend, device, assets, shaders, settings);
        _geometry = geometry;
        _unifiedViews = new RenderView[checked((int)outputs.ViewCapacity)];
    }

    public VirtualShadowMap ShadowMap => _shadowMap;

    public int UnifiedViewCount { get; private set; }

    /// <summary>
    /// Begins one VSM frame, builds receiver-plus-caster views, and stages them into the unified
    /// Cluster geometry pipeline. Returned VSM resources remain owned by this feature.
    /// </summary>
    public VirtualShadowMapFrameResources StageFrame(
        RenderLightSet lights,
        in RenderView receiverView,
        ulong geometryRevision,
        ulong materialMappingRevision,
        int instanceCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (_framePending)
            throw new InvalidOperationException("The previous VSM frame must be committed or discarded.");
        _unifiedViews[0] = receiverView;
        UnifiedViewCount = 1;
        VirtualShadowMapFrameResources resources = default;
        if (_shadowMap.Settings.Enabled)
        {
            resources = _shadowMap.BeginFrame();
            _shadowFramePending = true;
            _ = _shadowMap.PrepareViews(
                lights,
                receiverView,
                geometryRevision,
                materialMappingRevision,
                instanceCount);
            int shadowCount = _shadowMap.CopyPreparedRenderViews(
                _unifiedViews.AsSpan(1),
                receiverView.ViewportWidth,
                receiverView.ViewportHeight);
            UnifiedViewCount = checked(1 + shadowCount);
        }
        _geometry.StageViews(_unifiedViews.AsSpan(0, UnifiedViewCount));
        _framePending = true;
        return resources;
    }

    public void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_framePending)
            throw new InvalidOperationException("No VSM frame is waiting for commit.");
        if (_shadowFramePending)
            _shadowMap.Commit(completions);
        ClearFrame();
    }

    public void Discard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_shadowFramePending)
            _shadowMap.Discard();
        ClearFrame();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_shadowFramePending)
            _shadowMap.Discard();
        _shadowMap.Dispose();
        Array.Clear(_unifiedViews);
        UnifiedViewCount = 0;
        _framePending = false;
        _shadowFramePending = false;
        _disposed = true;
    }

    private void ClearFrame()
    {
        _unifiedViews.AsSpan(0, UnifiedViewCount).Clear();
        UnifiedViewCount = 0;
        _framePending = false;
        _shadowFramePending = false;
    }
}
