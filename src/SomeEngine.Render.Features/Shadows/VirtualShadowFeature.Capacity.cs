using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    /// <summary>Grows light-dependent storage before beginning the next graph frame.</summary>
    public void ReconcileLights(RenderLightSet lights)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = _shadowMap.Settings;
        uint lightCount = checked((uint)(lights.Directional.Count + lights.Points.Count + lights.Spots.Count));
        uint sourceViews = checked(lightCount * settings.MaximumViewsPerLight);
        if (sourceViews <= _sourceViewCapacity) return;
        uint tableEntries = checked(sourceViews * settings.PageTableEntriesPerView);
        uint hierarchyWords = checked(sourceViews * settings.PageHierarchyWordsPerView);
        uint rasterViews = checked(sourceViews * settings.MipLevelCount);
        RenderGraphEdit edit = _renderGraph.BeginEdit();
        try
        {
            GrowLightBuffer(ref edit, _requestedPages, (ulong)tableEntries * sizeof(uint), "Virtual shadow request epochs");
            GrowLightBuffer(ref edit, _requestedPageFlags, (ulong)tableEntries * sizeof(uint), "Virtual shadow request flags");
            GrowLightBuffer(ref edit, _requestedPageIndices, (ulong)tableEntries * sizeof(uint), "Virtual shadow requested page indices");
            GrowLightBuffer(ref edit, _pageReceiverMasks, (ulong)tableEntries * 2u * sizeof(uint), "Virtual shadow page receiver masks");
            GrowLightBuffer(ref edit, _uncachedPageRectBounds, (ulong)rasterViews * 4u * sizeof(uint), "Virtual shadow uncached page rect bounds");
            GrowLightBuffer(ref edit, _allocatedPageRectBounds, (ulong)rasterViews * 4u * sizeof(uint), "Virtual shadow allocated page rect bounds");
            GrowLightBuffer(ref edit, _uncachedPageHierarchy, (ulong)hierarchyWords * sizeof(uint), "Virtual shadow uncached page hierarchy");
            GrowLightBuffer(ref edit, _allocatedPageHierarchy, (ulong)hierarchyWords * sizeof(uint), "Virtual shadow allocated page hierarchy");
            GrowLightBuffer(ref edit, _policyRasterWorkCount, (1ul + 2ul * sourceViews) * sizeof(uint), "Virtual shadow policy raster work count");
            GrowLightBuffer(ref edit, _depthWorkCount, (ulong)sourceViews * 4u * sizeof(uint), "Virtual shadow static/dynamic visible work");
            GrowLightBuffer(ref edit, _rasterInvocationViews, (ulong)rasterViews * ClusterRenderContext.ViewSizeInBytes, "Virtual shadow raster invocation views");
            GrowLightBuffer(ref edit, _rasterTargetViews, (ulong)rasterViews * ClusterRenderContext.TargetViewSizeInBytes, "Virtual shadow raster target views");
            GrowPageSamplingStorage(ref edit, tableEntries);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        _sourceViewCapacity = sourceViews;
        _requestEpochEntryCapacity = tableEntries;
        _forceStateReset = true;
    }

    private static void GrowLightBuffer(ref RenderGraphEdit edit, GraphBufferId buffer, ulong size, string label)
        => edit.SetBufferDescription(buffer, new BufferDesc(checked(size),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination, label));

    private void GrowPageSamplingStorage(ref RenderGraphEdit edit, uint entries)
    {
        if (!_shadowMap.Settings.PrefilteredDistantEnabled) return;
        GrowLightBuffer(ref edit, _prefilterPreviousPages, (ulong)entries * 2u * sizeof(uint), "Virtual shadow prefilter page histories");
        GrowLightBuffer(ref edit, _prefilterPreviousMasks, (ulong)entries * 16u, "Virtual shadow prefilter receiver histories");
        _prefilterEntryCapacity = entries;
        _prefilterHistoryReady = false;
    }
}
