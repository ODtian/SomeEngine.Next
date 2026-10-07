using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private RasterInputs? _rasterInputs;

    private sealed class RasterInputs
    {
        internal readonly List<GraphBufferId> Buffers = [];
        internal readonly List<GraphBufferInput> Uniforms = [];
        internal GraphBufferInput? SoftwareUniforms;
        internal RasterBinning? PatchBinning;
        internal MeshRaster? MeshDraw;
    }

    private sealed record MeshRaster(GraphPassId Pass, uint Bin, GraphBufferAccessId Access,
        ClusterTessellationRasterPassData Data);

    private readonly record struct TessellationSplitKey(
        ClusterComputePipeline Pipeline, ClusterCommandBindingKey Bindings);

    private sealed record RasterBin(ClusterRasterInvocation Stage)
    {
        internal RasterInputs Inputs { get; } = new();
    }

    private sealed class RasterBinComparer : IEqualityComparer<RasterBin>
    {
        internal static readonly RasterBinComparer Instance = new();
        public bool Equals(RasterBin? left, RasterBin? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null &&
            SameRasterStage(left.Stage, right.Stage);
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
            left.SharedBins == right.SharedBins && SameRasterStage(left.Stage, right.Stage);
        public int GetHashCode(TessellationSplitStage value) => value.Stage.BinIndex.GetHashCode();
    }

    private sealed record TessellationGroup(
        GraphRegionMap<uint, RasterBin> Seeds,
        GraphRegionMap<int, TessellationSplitStage> Split);

    private sealed record RasterPhase(
        GraphRegionId Region,
        GraphRegionMap<uint, RasterBin> Bins,
        GraphRegionMap<TessellationSplitKey, bool> Groups,
        ImmutableDictionary<TessellationSplitKey, TessellationGroup> TessellationGroups,
        GraphRegionMap<int, RasterInputs> Patches,
        bool PhaseOne,
        bool PolicyTarget);

    private static bool SameRasterStage(ClusterRasterInvocation left, ClusterRasterInvocation right) =>
        left.BinIndex == right.BinIndex && ReferenceEquals(left.Material, right.Material) && left.Key.Equals(right.Key) &&
        left.CommandBindings.Equals(right.CommandBindings) && left.HasSameBindings(right);

    private void ReleaseRasterInputs(ref RenderGraphEdit graph, RasterInputs inputs)
    {
        foreach (GraphBufferId buffer in inputs.Buffers) RemoveBufferData(ref graph, buffer);
        foreach (GraphBufferInput uniform in inputs.Uniforms) RemoveParameters(uniform);
    }

    private GraphRegionMap<uint, RasterBin> ReconcileRasterBins(ref RenderGraphEdit graph,
        GraphRegionMap<uint, RasterBin> previous, ClusterRasterInvocation[] stages,
        GraphRegionBuilder<uint, RasterBin> build)
    {
        var desired = stages.ToDictionary(static stage => stage.BinIndex, static stage => new RasterBin(stage));
        foreach (RasterBin bin in previous.Values)
            if (!desired.TryGetValue(bin.Stage.BinIndex, out RasterBin? next) ||
                !RasterBinComparer.Instance.Equals(bin, next))
                ReleaseRasterInputs(ref graph, bin.Inputs);
        return previous.Reconcile(ref graph, desired, build);
    }

    private ImmutableDictionary<int, RasterPhase> ReconcileRasterPhases(ref RenderGraphEdit graph,
        ClusterRasterStageSet stages)
    {
        var phases = GraphData.RasterPhases;
        foreach ((int phase, RasterPhase data) in GraphData.RasterPhases)
            phases = phases.SetItem(phase, ReconcileRasterPhase(ref graph, data, stages));
        return phases;
    }

    private RasterPhase ReconcileRasterPhase(ref RenderGraphEdit graph, RasterPhase data,
        ClusterRasterStageSet stages)
    {
        ClusterRasterInvocation[] bins = stages.Invocations.ToArray();
        var splitStages = bins.Where(static stage => stage.Tessellation is not null)
            .GroupBy(static stage => new TessellationSplitKey(stage.Tessellation!.Split,
                stage.Tessellation.SplitBindings))
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var tessellationGroups = data.TessellationGroups;
        foreach ((TessellationSplitKey key, TessellationGroup group) in data.TessellationGroups)
        {
            if (splitStages.ContainsKey(key)) continue;
            foreach (RasterBin seed in group.Seeds.Values) ReleaseRasterInputs(ref graph, seed.Inputs);
            foreach (TessellationSplitStage split in group.Split.Values) ReleaseRasterInputs(ref graph, split.Inputs);
            tessellationGroups = tessellationGroups.Remove(key);
        }
        GraphRegionMap<TessellationSplitKey, bool> groups = data.Groups.Reconcile(ref graph,
            splitStages.Keys.Select(static key => new KeyValuePair<TessellationSplitKey, bool>(key, true)),
            (ref RenderGraphEdit edit, GraphRegionId region, TessellationSplitKey key, bool value) =>
            {
                GraphRegionId reset = edit.AddRegion("Reset", region);
                GraphRegionId seeds = edit.AddRegion("Seeds", region);
                GraphRegionId split = edit.AddRegion("Split", region);
                RecordTessellationLevelReset(ref edit, GraphData.Resources, splitStages[key][0].BinIndex,
                    data.PhaseOne, reset);
                tessellationGroups = tessellationGroups.Add(key, new TessellationGroup(
                    new GraphRegionMap<uint, RasterBin>(seeds, RasterBinComparer.Instance),
                    new GraphRegionMap<int, TessellationSplitStage>(split, TessellationSplitComparer.Instance)));
            });
        foreach ((TessellationSplitKey key, ClusterRasterInvocation[] seeds) in splitStages)
        {
            TessellationGroup group = tessellationGroups[key];
            GraphRegionMap<uint, RasterBin> seedRegions = ReconcileRasterBins(ref graph, group.Seeds, seeds,
                (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
                {
                    _rasterInputs = value.Inputs;
                    try { RecordTessellationSeed(ref edit, GraphData.Resources, value.Stage,
                        data.PhaseOne, data.PolicyTarget, region); }
                    finally { _rasterInputs = null; }
                });
            // Keep the representative while its bin survives; additional seeds reuse its bindings.
            TessellationSplitStage? before = group.Split.Values.FirstOrDefault();
            ClusterRasterInvocation representative = seeds.FirstOrDefault(stage =>
                stage.BinIndex == before?.Stage.BinIndex) ?? seeds[0];
            var next = new TessellationSplitStage(representative, seeds.Length > 1);
            if (before is not null && !TessellationSplitComparer.Instance.Equals(before, next))
                ReleaseRasterInputs(ref graph, before.Inputs);
            GraphRegionMap<int, TessellationSplitStage> splitRegions = group.Split.Reconcile(ref graph,
                [new KeyValuePair<int, TessellationSplitStage>(0, next)],
                (ref RenderGraphEdit edit, GraphRegionId region, int key, TessellationSplitStage value) =>
                {
                    _rasterInputs = value.Inputs;
                    try { RecordTessellationSplit(ref edit, GraphData.Resources, value.Stage,
                        data.PhaseOne, data.PolicyTarget, value.SharedBins, region); }
                    finally { _rasterInputs = null; }
                });
            tessellationGroups = tessellationGroups.SetItem(key, new TessellationGroup(seedRegions, splitRegions));
        }
        RasterInputs? patchInputs = data.Patches.Values.FirstOrDefault();
        if (splitStages.Count == 0 && patchInputs is not null) ReleaseRasterInputs(ref graph, patchInputs);
        GraphRegionMap<int, RasterInputs> patches = data.Patches.Reconcile(ref graph,
            splitStages.Count == 0 ? [] : [new KeyValuePair<int, RasterInputs>(0, patchInputs ?? new RasterInputs())],
            (ref RenderGraphEdit edit, GraphRegionId region, int key, RasterInputs value) =>
            {
                _rasterInputs = value;
                try { RecordTessellationBinning(ref edit, GraphData.Resources,
                    GraphData.Resources.TessellationFinalPatches, GraphData.Resources.TessellationWorkPatches,
                    GraphData.Resources.TessellationSourceDispatchArgs, 2u * sizeof(uint),
                    data.PhaseOne ? "phase one final patches" : "phase two final patches", region); }
                finally { _rasterInputs = null; }
            });
        GraphRegionMap<uint, RasterBin> binRegions = ReconcileRasterBins(ref graph, data.Bins, bins,
            (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
            {
                _rasterInputs = value.Inputs;
                try
                {
                    ClusterRasterInvocation[] single = [value.Stage];
                    if (_visibilityMode != ClusterVisibilityMode.HardwareOnly)
                        RecordTessellationDice(ref edit, GraphData.Resources, single, data.PhaseOne, data.PolicyTarget, region);
                    RecordSoftwareRaster(ref edit, GraphData.Resources, single, GraphData.Resources.PackedViews,
                        data.PhaseOne, data.PolicyTarget, region);
                    RecordTessellationHardwareRaster(ref edit, GraphData.Resources, single, data.PhaseOne, data.PolicyTarget, region);
                    RecordHardwareRaster(ref edit, GraphData.Resources, single, GraphData.Resources.PackedViews,
                        data.PhaseOne, data.PolicyTarget, region);
                }
                finally { _rasterInputs = null; }
            });
        return data with { Groups = groups, TessellationGroups = tessellationGroups, Patches = patches, Bins = binRegions };
    }


    private sealed record RasterBinning(
        GraphBufferInput Uniforms, GraphPassId Reset, GraphPassId Reserve, bool PolicyTarget);

    private void UpdateRasterBinning(ref RenderGraphEdit graph, ImmutableDictionary<int, RasterPhase> phases)
    {
        Span<ClusterRasterBinningUniforms> uniforms = stackalloc ClusterRasterBinningUniforms[1];
        foreach (RasterBinning? binning in GraphData.RasterBinning)
            if (binning is not null)
            {
                uniforms[0] = new ClusterRasterBinningUniforms
                {
                    RasterMaxBins = GraphData.Resources.RasterBinCount,
                    SlotCapacity = GraphData.Resources.SlotCapacity,
                    MaxVisibleClusters = _options.MaxCandidates,
                    CollectMetrics = _options.EnableFrameMetricsReadback ? 1u : 0u,
                    PolicyTarget = binning.PolicyTarget ? 1u : 0u,
                    ScreenWidth = checked((uint)GraphData.Resources.Width),
                    ScreenHeight = checked((uint)GraphData.Resources.Height),
                    ViewCount = GraphData.Resources.ViewCount,
                    BinnedEntriesPerCluster = GraphData.Resources.BinnedEntriesPerCluster,
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
                    };
                    UpdateParameters(ref graph, uniforms, MemoryMarshal.AsBytes(software));
                }
                if (bin.Inputs.MeshDraw is { } mesh)
                    graph.SetBufferAccess(mesh.Access, GraphAccessMode.Read, WriteCoverage.Partial,
                        PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument,
                        new BufferRange(MeshRasterOffset(mesh.Bin), ClusterIndirectAbi.DispatchBytes));
            }
    }

    private void UpdateRasterDispatches(ref RenderGraphFrame graph)
    {
        foreach (RasterPhase phase in GraphData.RasterPhases.Values)
        {
            if (phase.TessellationGroups.IsEmpty) continue;
            foreach (RasterBin bin in phase.Bins.Values)
                if (bin.Inputs.MeshDraw is { } mesh)
                    graph.SetPassDataIfChanged(mesh.Pass, mesh.Data with { IndirectOffset = MeshRasterOffset(mesh.Bin) });
        }
        var reset = new ClusterDispatchParameters(new DispatchArguments(
            Math.Max(1u, (GraphData.Resources.RasterBinCount + 63u) / 64u), 1u, 1u));
        var reserve = new ClusterDispatchParameters(new DispatchArguments(
            Math.Max(1u, (GraphData.Resources.RasterBinCount + 127u) / 128u), 1u, 1u));
        foreach (RasterBinning? binning in GraphData.RasterBinning)
        {
            if (binning is null) continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
        }
        uint patchBinCount = checked(GraphData.Resources.RasterBinCount * 8u);
        reset = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (patchBinCount + 63u) / 64u), 1u, 1u));
        reserve = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (patchBinCount + 127u) / 128u), 1u, 1u));
        foreach (RasterPhase phase in GraphData.RasterPhases.Values)
        {
            if (phase.Patches.Values.FirstOrDefault()?.PatchBinning is not { } binning) continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
        }
    }
}
