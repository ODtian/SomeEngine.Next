using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    /// <summary>Removes this shadow set after its Cluster geometry and output consumers have detached.</summary>
    public void UninstallGraph()
    {
        using RenderGraphEdit edit = _renderGraph.BeginEdit();
        foreach (GraphRegionId region in _graphRegions) edit.Remove(region);
        foreach (GraphBufferId buffer in new[]
        {
            _pageOwners, _pageMetadata, _pageLists, _requestedPageFlags, _requestedPageIndices,
            _requestedPageCount, _activePages, _activePageCount, _uncachedPageRectBounds,
            _uncachedPageHierarchy, _allocatedPageHierarchy, _allocatedPageRectBounds,
            _clusterInvocationDispatchArguments, _pageWorkDispatchArguments, _pageRequestDispatchArguments,
            _allocationFailures, _policyRasterWorkCount, _depthWorkCount, _dynamicResolutionBias,
            _frameResolutionBias, _rasterInvocationViews, _rasterTargetViews, _initUniforms.Buffer,
            _qualityReceiverCoverage, _prefilterPages, _prefilterArguments, _prefilterPreviousPages,
            _previousWorkCounts, _previousWorkPressure, _frameWorkPressure,
            _workTimeResults, _invalidationTimingResults,
        })
            if (buffer != default) edit.Remove(buffer);
        foreach (GraphTextureId texture in new[] { _atlas, _pageHiZ, _pageReceiverMasks,
            _prefilterAxisX, _prefilterSeparate, _prefilterMerged, _prefilterPreviousMasks })
            if (texture != default) edit.Remove(texture);
        if (_workTimeQueryId != default) edit.Remove(_workTimeQueryId);
        if (_invalidationTimingQueryId != default) edit.Remove(_invalidationTimingQueryId);
        edit.Commit();
    }
}
