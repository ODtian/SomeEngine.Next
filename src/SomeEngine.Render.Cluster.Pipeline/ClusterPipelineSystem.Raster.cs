using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Render.Assets;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    public const string RasterTimeGroup = "Cluster raster";

    private RasterInputs? _rasterInputs;

    private sealed class RasterInputs
    {
        internal ImmutableArray<IndexedTextureRead> TextureReads = [];
        internal readonly List<GraphBufferSlice> Uniforms = [];
        internal GraphBufferSlice? SoftwareUniforms;
        internal RasterBinning? PatchBinning;
        internal MeshRaster? MeshDraw;
    }

    private sealed record MeshRaster(GraphPassId Pass, GraphBufferAccessId Access);

    private readonly record struct TessellationSplitKey(
        LinkedComputePipeline Pipeline, ClusterCommandBindingKey Bindings);

    private sealed record RasterBin(ClusterRasterInvocation Stage)
    {
        internal RasterInputs Inputs { get; } = new();
    }

    private sealed class RasterBinComparer : IEqualityComparer<RasterBin>
    {
        internal static readonly RasterBinComparer Instance = new();
        public bool Equals(RasterBin? left, RasterBin? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null &&
            SameRasterStage(left.Stage, right.Stage) &&
            left.Inputs.TextureReads.AsSpan().SequenceEqual(right.Inputs.TextureReads.AsSpan());
        public int GetHashCode(RasterBin value) => value.Stage.BinIndex.GetHashCode();
    }

    private sealed record TessellationSplitStage(ClusterRasterInvocation Stage, bool SharedBins)
    {
        internal RasterInputs Inputs { get; } = new();
    }

    private sealed class TessellationSplitComparer : IEqualityComparer<TessellationSplitStage>
    {
        internal static readonly TessellationSplitComparer Instance = new();
        public bool Equals(TessellationSplitStage? left, TessellationSplitStage? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null &&
            left.SharedBins == right.SharedBins && SameRasterStage(left.Stage, right.Stage) &&
            left.Inputs.TextureReads.AsSpan().SequenceEqual(right.Inputs.TextureReads.AsSpan());
        public int GetHashCode(TessellationSplitStage value) => value.Stage.BinIndex.GetHashCode();
    }

    private sealed record TessellationGroup(
        GraphRegionMap<uint, RasterBin> Seeds,
        GraphRegionMap<int, TessellationSplitStage> Split);

    private sealed record RasterPhase(
        GraphRegionMap<uint, RasterBin> Bins,
        GraphRegionMap<TessellationSplitKey, bool> Groups,
        ImmutableDictionary<TessellationSplitKey, TessellationGroup> TessellationGroups,
        GraphRegionMap<int, RasterInputs> Patches,
        bool PhaseOne,
        bool PolicyTarget)
    {
        internal GraphRegionMap<uint, RasterBin> Deformation { get; init; } = null!;
        internal DeformDrawData DrawData { get; init; }
    }

    private static bool SameRasterStage(ClusterRasterInvocation left, ClusterRasterInvocation right) =>
        left.BinIndex == right.BinIndex && ReferenceEquals(left.Material, right.Material) &&
        left.UsesEarlyDepth == right.UsesEarlyDepth && left.ReadsMaterialScalars == right.ReadsMaterialScalars &&
        ReferenceEquals(left.Software, right.Software) && ReferenceEquals(left.Hardware, right.Hardware) &&
        ReferenceEquals(left.Tessellation?.Seed, right.Tessellation?.Seed) &&
        ReferenceEquals(left.Tessellation?.Split, right.Tessellation?.Split) &&
        ReferenceEquals(left.Tessellation?.Dice, right.Tessellation?.Dice) &&
        ReferenceEquals(left.Tessellation?.Hardware, right.Tessellation?.Hardware) &&
        ReferenceEquals(left.TargetStages?.Software, right.TargetStages?.Software) &&
        ReferenceEquals(left.TargetStages?.Hardware, right.TargetStages?.Hardware) &&
        left.CommandBindings.Equals(right.CommandBindings) && left.HasSameBindings(right);

    private void ReleaseRasterInputs(RasterInputs inputs)
    {
        foreach (GraphBufferSlice uniform in inputs.Uniforms) RemoveParameters(uniform);
    }

    private GraphRegionMap<uint, RasterBin> ReconcileRasterBins(ref RenderGraphEdit graph,
        GraphRegionMap<uint, RasterBin> previous, RasterBin[] bins,
        GraphRegionBuilder<uint, RasterBin> build)
    {
        var desired = bins.ToDictionary(static bin => bin.Stage.BinIndex,
            static bin => new RasterBin(bin.Stage) { Inputs = { TextureReads = bin.Inputs.TextureReads } });
        foreach (RasterBin bin in previous.Values)
            if (!desired.TryGetValue(bin.Stage.BinIndex, out RasterBin? next) ||
                !RasterBinComparer.Instance.Equals(bin, next))
                ReleaseRasterInputs(bin.Inputs);
        return previous.Reconcile(ref graph, desired, build);
    }

    private ImmutableDictionary<int, RasterPhase> ReconcileRasterPhases(ref RenderGraphEdit graph,
        ClusterRasterStageSet stages, ClusterShadeStageSet shadeStages)
    {
        RasterBin[] bins = CreateRasterBins(stages, shadeStages);
        var splitStages = CreateTessellationSplitStages(bins);
        var phases = GraphData.RasterPhases;
        foreach ((int phase, RasterPhase data) in GraphData.RasterPhases)
            phases = phases.SetItem(phase, ReconcileRasterPhase(ref graph, phase, data, bins, splitStages));
        return phases;
    }

    private RasterBin[] CreateRasterBins(ClusterRasterStageSet stages, ClusterShadeStageSet shadeStages)
    {
        var reads = CollectIndexedTextureReads(stages.BinIndices, shadeStages, 0x07ffffffu);
        return stages.Invocations.Select(stage => new RasterBin(stage)
            { Inputs = { TextureReads = stage.ReadsMaterialScalars ? reads.GetValueOrDefault(stage.BinIndex, []) : [] } }).ToArray();
    }

    private static Dictionary<TessellationSplitKey,
        (RasterBin[] Seeds, ImmutableArray<IndexedTextureRead> TextureReads)> CreateTessellationSplitStages(RasterBin[] bins) =>
        bins.Where(static bin => bin.Stage.Tessellation is not null)
            .GroupBy(static bin => new TessellationSplitKey(bin.Stage.Tessellation!.Split,
                bin.Stage.Tessellation.SplitBindings))
            .ToDictionary(static group => group.Key,
                static group => (Seeds: group.ToArray(), TextureReads: default(ImmutableArray<IndexedTextureRead>)));

    private RasterPhase ReconcileRasterPhase(ref RenderGraphEdit graph, int phase, RasterPhase data,
        RasterBin[] bins, Dictionary<TessellationSplitKey,
            (RasterBin[] Seeds, ImmutableArray<IndexedTextureRead> TextureReads)> splitStages)
    {
        var deformation = ReconcileRasterBins(ref graph, data.Deformation,
            bins.Where(static bin => bin.Stage.Deformation is not null).ToArray(),
            (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
            {
                _rasterInputs = value.Inputs;
                try { RecordDeformation(ref edit, value.Stage, data.DrawData, region); }
                finally { _rasterInputs = null; }
            });
        bool phaseOne = (phase & 1) == 0;
        bool policyTarget = phase >= 2;
        var tessellationGroups = data.TessellationGroups;
        foreach ((TessellationSplitKey key, TessellationGroup group) in data.TessellationGroups)
        {
            if (splitStages.ContainsKey(key)) continue;
            foreach (RasterBin seed in group.Seeds.Values) ReleaseRasterInputs(seed.Inputs);
            foreach (TessellationSplitStage split in group.Split.Values) ReleaseRasterInputs(split.Inputs);
            tessellationGroups = tessellationGroups.Remove(key);
        }
        GraphRegionMap<TessellationSplitKey, bool> groups = data.Groups.Reconcile(ref graph,
            splitStages.Keys.Select(static key => new KeyValuePair<TessellationSplitKey, bool>(key, true)),
            (ref RenderGraphEdit edit, GraphRegionId region, TessellationSplitKey key, bool value) =>
            {
                GraphRegionId reset = edit.AddRegion("Reset", region);
                GraphRegionId seeds = edit.AddRegion("Seeds", region);
                GraphRegionId split = edit.AddRegion("Split", region);
                RecordTessellationLevelReset(ref edit, GraphData.Resources, splitStages[key].Seeds[0].Stage.BinIndex,
                    phaseOne, reset);
                tessellationGroups = tessellationGroups.Add(key, new TessellationGroup(
                    new GraphRegionMap<uint, RasterBin>(seeds, RasterBinComparer.Instance),
                    new GraphRegionMap<int, TessellationSplitStage>(split, TessellationSplitComparer.Instance)));
            });
        foreach (var (key, split) in splitStages)
        {
            RasterBin[] seeds = split.Seeds;
            TessellationGroup group = tessellationGroups[key];
            GraphRegionMap<uint, RasterBin> seedRegions = ReconcileRasterBins(ref graph, group.Seeds, seeds,
                (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
                {
                    _rasterInputs = value.Inputs;
                    try { RecordTessellationSeed(ref edit, GraphData.Resources, value.Stage,
                        phaseOne, policyTarget, region); }
                    finally { _rasterInputs = null; }
                });
            // Keep the representative while its bin survives; additional seeds reuse its bindings.
            TessellationSplitStage? before = group.Split.Values.FirstOrDefault();
            ClusterRasterInvocation representative = seeds.Select(static bin => bin.Stage).FirstOrDefault(stage =>
                stage.BinIndex == before?.Stage.BinIndex) ?? seeds[0].Stage;
            var next = new TessellationSplitStage(representative, seeds.Length > 1);
            ImmutableArray<IndexedTextureRead> textureReads = split.TextureReads;
            if (textureReads.IsDefault)
            {
                textureReads = seeds.SelectMany(static bin => bin.Inputs.TextureReads).Distinct()
                    .OrderBy(static read => read.Material.MaterialId).ThenBy(static read => read.Property).ToImmutableArray();
                splitStages[key] = (seeds, textureReads);
            }
            next.Inputs.TextureReads = textureReads;
            if (before is not null && !TessellationSplitComparer.Instance.Equals(before, next))
                ReleaseRasterInputs(before.Inputs);
            GraphRegionMap<int, TessellationSplitStage> splitRegions = group.Split.Reconcile(ref graph,
                [new KeyValuePair<int, TessellationSplitStage>(0, next)],
                (ref RenderGraphEdit edit, GraphRegionId region, int key, TessellationSplitStage value) =>
                {
                    _rasterInputs = value.Inputs;
                    try { RecordTessellationSplit(ref edit, GraphData.Resources, value.Stage,
                        phaseOne, policyTarget, value.SharedBins, region); }
                    finally { _rasterInputs = null; }
                });
            tessellationGroups = tessellationGroups.SetItem(key, new TessellationGroup(seedRegions, splitRegions));
        }
        RasterInputs? patchInputs = data.Patches.Values.FirstOrDefault();
        if (splitStages.Count == 0 && patchInputs is not null) ReleaseRasterInputs(patchInputs);
        GraphRegionMap<int, RasterInputs> patches = data.Patches.Reconcile(ref graph,
            splitStages.Count == 0 ? [] : [new KeyValuePair<int, RasterInputs>(0, patchInputs ?? new RasterInputs())],
            (ref RenderGraphEdit edit, GraphRegionId region, int key, RasterInputs value) =>
            {
                _rasterInputs = value;
                try { RecordTessellationBinning(ref edit, GraphData.Resources,
                    GraphData.Resources.TessellationFinalPatches, GraphData.Resources.TessellationWorkPatches,
                    GraphData.Resources.TessellationSourceDispatchArgs, 2u * sizeof(uint),
                    phaseOne ? "phase one final patches" : "phase two final patches", region); }
                finally { _rasterInputs = null; }
            });
        GraphRegionMap<uint, RasterBin> binRegions = ReconcileRasterBins(ref graph, data.Bins, bins,
            (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
            {
                _rasterInputs = value.Inputs;
                try
                {
                    if (_visibilityMode != ClusterVisibilityMode.HardwareOnly)
                        RecordTessellationDice(ref edit, GraphData.Resources, value.Stage, phaseOne, policyTarget, region);
                    RecordSoftwareRaster(ref edit, GraphData.Resources, value.Stage,
                        phaseOne, policyTarget, region);
                    RecordTessellationHardwareRaster(ref edit, GraphData.Resources, value.Stage, phaseOne, policyTarget, region);
                    RecordHardwareRaster(ref edit, GraphData.Resources, value.Stage,
                        phaseOne, policyTarget, region);
                }
                finally { _rasterInputs = null; }
            });
        return data with { Deformation = deformation, Groups = groups, TessellationGroups = tessellationGroups, Patches = patches, Bins = binRegions };
    }


    private sealed record RasterBinning(
        GraphBufferSlice Uniforms, GraphPassId Reset, GraphPassId Reserve);

    private void UpdateRasterBinning(ref RenderGraphEdit graph, ImmutableDictionary<int, RasterPhase> phases)
    {
        Span<ClusterRasterBinningUniforms> uniforms = stackalloc ClusterRasterBinningUniforms[1];
        for (int phase = 0; phase < GraphData.RasterBinning.Length; phase++)
            if (GraphData.RasterBinning[phase] is { } binning)
            {
                uniforms[0] = new ClusterRasterBinningUniforms
                {
                    RasterMaxBins = GraphData.Resources.RasterBinCount,
                    SlotCapacity = GraphData.Resources.SlotCapacity,
                    MaxVisibleClusters = _options.MaxCandidates,
                    CollectMetrics = _options.EnableFrameMetricsReadback ? 1u : 0u,
                    PolicyTarget = phase >= 2 ? 1u : 0u,
                    ScreenWidth = checked((uint)GraphData.Resources.Width),
                    ScreenHeight = checked((uint)GraphData.Resources.Height),
                    ViewCount = GraphData.Resources.ViewCount,
                    BinnedEntriesPerCluster = GraphData.Resources.BinnedEntriesPerCluster,
                    DepthBucketsEnabled = GraphData.Resources.DepthBucketsEnabled ? 1u : 0u,
                };
                UpdateParameters(ref graph, binning.Uniforms, MemoryMarshal.AsBytes(uniforms));
            }
        Span<ClusterTessellationBinUniforms> patches = stackalloc ClusterTessellationBinUniforms[1];
        patches[0] = new ClusterTessellationBinUniforms
        {
            BinCount = checked(GraphData.Resources.RasterBinCount * 8u),
            MaxPatchItems = _options.MaxCandidates,
            SourceCounterOffset = 2u * sizeof(uint),
        };
        foreach (RasterPhase phase in phases.Values)
            if (phase.Patches.Values.FirstOrDefault()?.PatchBinning is { } binning)
                UpdateParameters(ref graph, binning.Uniforms, MemoryMarshal.AsBytes(patches));
    }

    private ulong MeshRasterOffset(uint bin) => checked((ulong)GraphData.Resources.RasterBinCount * 8u *
        ClusterIndirectAbi.DrawIndexedBytes + (ulong)bin * ClusterIndirectAbi.DispatchBytes);

    private void UpdateRasterCapacity(ref RenderGraphEdit graph, uint previousCount,
        ImmutableDictionary<int, RasterPhase> phases)
    {
        if (previousCount == GraphData.Resources.RasterBinCount) return;
        Span<ClusterSoftwareRasterUniforms> software = stackalloc ClusterSoftwareRasterUniforms[1];
        foreach (RasterPhase phase in phases.Values)
            foreach (RasterBin bin in phase.Bins.Values)
            {
                if (bin.Inputs.SoftwareUniforms is { } uniforms)
                {
                    software[0] = new ClusterSoftwareRasterUniforms
                    {
                        ScreenWidth = checked((uint)GraphData.Resources.Width),
                        ScreenHeight = checked((uint)GraphData.Resources.Height),
                        MaxBins = GraphData.Resources.RasterBinCount,
                        DebugDump = _options.EnableFrameMetricsReadback ? 1u : 0u,
                        CurrentBin = bin.Stage.BinIndex,
                        EarlyDepthEnabled = bin.Stage.UsesEarlyDepth ? 1u : 0u,
                    };
                    UpdateParameters(ref graph, uniforms, MemoryMarshal.AsBytes(software));
                }
                if (bin.Inputs.MeshDraw is { } mesh)
                    graph.SetBufferAccess(mesh.Access, GraphAccessMode.Read, WriteCoverage.Partial,
                        PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument,
                        new BufferRange(MeshRasterOffset(bin.Stage.BinIndex), ClusterIndirectAbi.DispatchBytes));
            }
    }

    private void UpdateRasterDispatches(ref RenderGraphFrame graph)
    {
        foreach (RasterPhase phase in GraphData.RasterPhases.Values)
        {
            if (phase.TessellationGroups.IsEmpty) continue;
            foreach (RasterBin bin in phase.Bins.Values)
                if (bin.Inputs.MeshDraw is { } mesh)
                    graph.SetPassDataIfChanged(mesh.Pass, MeshRasterOffset(bin.Stage.BinIndex));
        }
        var reset = new DispatchArguments(
            Math.Max(1u, (GraphData.Resources.RasterBinCount + 63u) / 64u), 1u, 1u);
        var reserve = new DispatchArguments(
            Math.Max(1u, (GraphData.Resources.RasterBinCount + 3u) / 4u), 1u, 1u);
        foreach (RasterBinning? binning in GraphData.RasterBinning)
        {
            if (binning is null) continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
        }
        uint patchBinCount = checked(GraphData.Resources.RasterBinCount * 8u);
        reset = new DispatchArguments(Math.Max(1u, (patchBinCount + 63u) / 64u), 1u, 1u);
        reserve = new DispatchArguments(Math.Max(1u, (patchBinCount + 127u) / 128u), 1u, 1u);
        foreach (RasterPhase phase in GraphData.RasterPhases.Values)
        {
            if (phase.Patches.Values.FirstOrDefault()?.PatchBinning is not { } binning) continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
        }
    }
}
