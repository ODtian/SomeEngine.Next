using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterGeometryPipeline
{
    private void RecordLayerViews(ref RenderGraphFrame frame, scoped ReadOnlySpan<ClusterViewUniforms> views, int instanceCount)
    {
        ClusterGraphInstaller.ClusterGraphData data = _installer.GraphData;
        frame.SetBufferData(data.Resources.LayerPackedViews, MemoryMarshal.AsBytes(views));
        uint count = checked((uint)views.Length);
        uint groups = RenderRasterContext.GetViewWorkGroupCount(count);
        frame.SetPassData(data.LayerSeed.Pass, new ClusterDispatchParameters(new DispatchArguments(
            Math.Max(1u, (checked((uint)instanceCount) + 63u) / 64u), Math.Min(groups, 65535u), (groups + 65534u) / 65535u)));
        _installer.WriteUniform(frame.GetBufferDataWriteSpan(data.ShaderParameterBytes), new ClusterTraversalSeedUniforms
        {
            ViewCount = count, MaxNodes = _options.MaxCandidates,
            UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
        }, data.LayerSeedUniforms);
    }

    public RenderGeometryResources LayerGeometry => _installer.GraphData.LayerGeometry;
    public GraphBufferId LayerTileWindows => _installer.GraphData.Resources.LayerTileWindows;
    public GraphBufferId RasterStageIndices => _installer.GraphData.Resources.RasterBinIndices;

    public IEnumerable<RenderRasterStage> RasterStages
    {
        get
        {
            foreach (ClusterRasterDispatch stage in _installer._installedRasterStages!.Dispatches)
            {
                RenderMaterialEntry material = _shading.Materials.First(value => ReferenceEquals(value.Material, stage.Material));
                yield return new(stage.BinIndex, material, stage.Tessellation is not null);
            }
        }
    }

    /// <summary>Connects layer tiles to the shared geometry work and its indexed terminal draw.</summary>
    public void ConfigureLayerTiles(uint tileSize, GraphBufferInput rasterMaterials,
        RenderTessellationRasterRegistration registerTessellation, uint hardwareWindowTiles = 64)
    {
        if (_installer.GraphData.LayerTileSize == tileSize &&
            _installer.GraphData.LayerHardwareWindowTiles == hardwareWindowTiles &&
            ReferenceEquals(_installer.GraphData.LayerRasterMaterials, rasterMaterials) &&
            _installer.GraphData.LayerTessellation == registerTessellation) return;
        if (_installer._graphInstalled)
        {
            _installer.EditGraph((ref RenderGraphEdit edit) =>
            {
                _installer.GraphData.LayerTileSize = tileSize;
                _installer.GraphData.LayerHardwareWindowTiles = hardwareWindowTiles;
                _installer.GraphData.LayerRasterMaterials = rasterMaterials;
                _installer.GraphData.LayerTessellation = registerTessellation;
                _installer.InstallLayers(ref edit, _installer._installedRasterStages!, _installer._installedShadeStages!);
            });
        }
        else
        {
            _installer.GraphData.LayerTileSize = tileSize;
            _installer.GraphData.LayerHardwareWindowTiles = hardwareWindowTiles;
            _installer.GraphData.LayerRasterMaterials = rasterMaterials;
            _installer.GraphData.LayerTessellation = registerTessellation;
        }
    }
}
