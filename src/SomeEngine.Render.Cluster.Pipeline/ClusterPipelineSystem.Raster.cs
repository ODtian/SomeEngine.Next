using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private ImmutableDictionary<int, RasterEpoch> _rasterEpochs = ImmutableDictionary<int, RasterEpoch>.Empty;
    private RasterPassGroup? _rasterGroup;
    private ImmutableDictionary<GraphPassId, MeshRaster> _meshRasterPasses = ImmutableDictionary<GraphPassId, MeshRaster>.Empty;
    private sealed record MeshRaster(uint Bin, GraphBufferAccessId Access, ClusterTessellationRasterPassData Data);

    private sealed class RasterPassGroup
    {
        internal readonly List<GraphPassId> Passes = [];
        internal readonly List<GraphBufferId> Uploads = [];
        internal readonly List<GraphBufferInput> Uniforms = [];
        internal readonly List<GraphBufferInput> SplitUniforms = [];
        internal GraphBufferInput? SoftwareUniforms;
        internal RasterBinning? PatchBinning;
        internal GraphPassId First => Passes[0];
        internal GraphPassId Last => Passes[^1];
    }

    private readonly record struct TessellationSplitKey(
        ClusterComputePipeline Pipeline, ClusterCommandBindingKey Bindings);

    private sealed record RasterBin(ClusterRasterInvocation Stage, RasterPassGroup Passes);
    private sealed record TessellationSeed(RasterPassGroup Passes, uint? Previous, uint? Next);
    private sealed record TessellationGroup(
        RasterPassGroup Reset,
        RasterPassGroup Split,
        ImmutableDictionary<uint, TessellationSeed> Seeds,
        uint? FirstSeed,
        uint? LastSeed,
        TessellationSplitKey? Previous,
        TessellationSplitKey? Next);
    private sealed record RasterEpoch(
        RasterPassGroup Work,
        RasterPassGroup Patches,
        ImmutableDictionary<uint, RasterBin> Bins,
        ImmutableDictionary<TessellationSplitKey, TessellationGroup> Splits,
        TessellationSplitKey? FirstSplit,
        TessellationSplitKey? LastSplit,
        ImmutableHashSet<GraphPassId> Consumers,
        bool PhaseOne,
        bool PolicyTarget);

    private void RemoveRasterPasses(ref RenderGraphEdit graph, RasterPassGroup group)
    {
        foreach (GraphPassId pass in group.Passes)
        {
            graph.Remove(pass);
            _materialInputs = _materialInputs.Remove(pass);
            _pageHeapReaderPasses = _pageHeapReaderPasses.Remove(pass);
            _meshRasterPasses = _meshRasterPasses.Remove(pass);
        }
        foreach (GraphBufferId buffer in group.Uploads)
            _uploads!.Remove(ref graph, buffer);
        foreach (GraphBufferInput uniform in group.Uniforms)
            _uploads!.RemoveUniform(uniform);
    }

    private static void OrderRasterPasses(ref RenderGraphEdit graph, RasterPassGroup group, GraphPassId predecessor)
    {
        foreach (GraphPassId pass in group.Passes)
        {
            graph.OrderAfter(pass, predecessor);
            predecessor = pass;
        }
    }

    private void AddRasterConsumer(ref RenderGraphEdit graph, int epoch, GraphPassId consumer)
    {
        RasterEpoch data = _rasterEpochs[epoch];
        if (data.Consumers.Contains(consumer)) return;
        graph.OrderAfter(consumer, data.Patches.Passes.Count == 0 ? data.Work.Last : data.Patches.Last);
        foreach (RasterBin bin in data.Bins.Values)
            foreach (GraphPassId pass in bin.Passes.Passes)
                graph.OrderAfter(consumer, pass);
        _rasterEpochs = _rasterEpochs.SetItem(epoch, data with { Consumers = data.Consumers.Add(consumer) });
    }

    private void UpdateRasterGroups(ref RenderGraphEdit graph, int epoch, ClusterRasterStageSet stages, bool initial)
    {
        RasterEpoch data = _rasterEpochs[epoch];
        IEnumerable<uint> changes = initial ? stages.Invocations.Select(static stage => stage.BinIndex) : stages.ChangedBins;
        var changedSplits = new HashSet<TessellationSplitKey>();
        foreach (uint bin in changes)
        {
            stages.TryGetBin(bin, out ClusterRasterInvocation? stage);
            if (data.Bins.TryGetValue(bin, out RasterBin? previous))
            {
                if (stage is not null && previous.Stage.Key.Equals(stage.Key) &&
                    previous.Stage.CommandBindings.Equals(stage.CommandBindings) && previous.Stage.HasSameBindings(stage))
                    continue;
                RemoveRasterPasses(ref graph, previous.Passes);
                if (previous.Stage.Tessellation is { } oldTessellation)
                {
                    var key = new TessellationSplitKey(oldTessellation.Split, oldTessellation.SplitBindings);
                    data = RemoveTessellationSeed(ref graph, data, key, bin);
                    changedSplits.Add(key);
                }
                data = data with { Bins = data.Bins.Remove(bin) };
            }
            if (stage is null) continue;
            if (stage.Tessellation is { } tessellation)
            {
                var key = new TessellationSplitKey(tessellation.Split, tessellation.SplitBindings);
                data = AddTessellationSeed(ref graph, data, key, stage);
                changedSplits.Add(key);
            }
            var passes = new RasterPassGroup();
            _rasterGroup = passes;
            try
            {
                ClusterRasterInvocation[] single = [stage];
                if (_visibilityMode != ClusterVisibilityMode.HardwareOnly)
                    RecordTessellationDice(ref graph, _frameResources, single, data.PhaseOne, data.PolicyTarget);
                RecordSoftwareRaster(ref graph, _frameResources, single, _frameResources.PackedViews, data.PhaseOne, data.PolicyTarget);
                RecordTessellationHardwareRaster(ref graph, _frameResources, single, data.PhaseOne, data.PolicyTarget);
                RecordHardwareRaster(ref graph, _frameResources, single, _frameResources.PackedViews, data.PhaseOne, data.PolicyTarget);
            }
            finally { _rasterGroup = null; }
            foreach (GraphPassId pass in passes.Passes)
            {
                graph.OrderAfter(pass, stage.Tessellation is null ? data.Work.Last : data.Patches.Last);
                foreach (GraphPassId consumer in data.Consumers) graph.OrderAfter(consumer, pass);
            }
            data = data with { Bins = data.Bins.SetItem(bin, new RasterBin(stage, passes)) };
        }
        foreach (TessellationSplitKey key in changedSplits)
        {
            TessellationGroup group = data.Splits[key];
            if (group.FirstSeed is null)
                data = RemoveTessellationGroup(ref graph, data, key);
            else
                UpdateTessellationSplit(ref graph, data, group);
        }
        _rasterEpochs = _rasterEpochs.SetItem(epoch, data);
    }

    private RasterEpoch AddTessellationSeed(ref RenderGraphEdit graph, RasterEpoch data,
        TessellationSplitKey key, ClusterRasterInvocation stage)
    {
        if (!data.Splits.TryGetValue(key, out TessellationGroup? group))
        {
            if (data.Patches.Passes.Count == 0)
            {
                var patches = new RasterPassGroup();
                _rasterGroup = patches;
                try
                {
                    RecordTessellationBinning(ref graph, _frameResources, _frameResources.TessellationFinalPatches,
                        _frameResources.TessellationWorkPatches, _frameResources.TessellationSourceDispatchArgs,
                        2u * sizeof(uint), data.PhaseOne ? "phase one final patches" : "phase two final patches");
                }
                finally { _rasterGroup = null; }
                OrderRasterPasses(ref graph, patches, data.Work.Last);
                foreach (GraphPassId consumer in data.Consumers)
                {
                    graph.RemoveOrder(consumer, data.Work.Last);
                    graph.OrderAfter(consumer, patches.Last);
                }
                data = data with { Patches = patches };
            }
            var reset = new RasterPassGroup();
            var split = new RasterPassGroup();
            _rasterGroup = reset;
            try
            {
                RecordTessellationLevelReset(ref graph, _frameResources, stage.BinIndex, data.PhaseOne);
                _rasterGroup = split;
                RecordTessellationSplit(ref graph, _frameResources, stage, reset.Last,
                    data.PhaseOne, data.PolicyTarget, false);
            }
            finally { _rasterGroup = null; }
            GraphPassId predecessor = data.LastSplit is { } last ? data.Splits[last].Split.Last : data.Work.Last;
            graph.RemoveOrder(data.Patches.First, predecessor);
            graph.OrderAfter(reset.First, predecessor);
            graph.OrderAfter(data.Patches.First, split.Last);
            group = new TessellationGroup(reset, split, ImmutableDictionary<uint, TessellationSeed>.Empty,
                null, null, data.LastSplit, null);
            if (data.LastSplit is { } previous)
                data = data with { Splits = data.Splits.SetItem(previous, data.Splits[previous] with { Next = key }) };
            data = data with { Splits = data.Splits.Add(key, group), FirstSplit = data.FirstSplit ?? key, LastSplit = key };
        }
        GraphPassId seedPredecessor = group.LastSeed is { } lastSeed ? group.Seeds[lastSeed].Passes.Last : group.Reset.Last;
        var seed = new RasterPassGroup();
        _rasterGroup = seed;
        try { RecordTessellationSeed(ref graph, _frameResources, stage, seedPredecessor, data.PhaseOne, data.PolicyTarget); }
        finally { _rasterGroup = null; }
        graph.RemoveOrder(group.Split.First, seedPredecessor);
        graph.OrderAfter(group.Split.First, seed.Last);
        var seeds = group.Seeds;
        if (group.LastSeed is { } previousSeed)
            seeds = seeds.SetItem(previousSeed, seeds[previousSeed] with { Next = stage.BinIndex });
        seeds = seeds.Add(stage.BinIndex, new TessellationSeed(seed, group.LastSeed, null));
        group = group with { Seeds = seeds, FirstSeed = group.FirstSeed ?? stage.BinIndex, LastSeed = stage.BinIndex };
        return data with { Splits = data.Splits.SetItem(key, group) };
    }

    private RasterEpoch RemoveTessellationSeed(ref RenderGraphEdit graph, RasterEpoch data,
        TessellationSplitKey key, uint bin)
    {
        TessellationGroup group = data.Splits[key];
        TessellationSeed seed = group.Seeds[bin];
        GraphPassId before = seed.Previous is { } previous ? group.Seeds[previous].Passes.Last : group.Reset.Last;
        GraphPassId after = seed.Next is { } next ? group.Seeds[next].Passes.First : group.Split.First;
        graph.OrderAfter(after, before);
        RemoveRasterPasses(ref graph, seed.Passes);
        var seeds = group.Seeds.Remove(bin);
        if (seed.Previous is { } p) seeds = seeds.SetItem(p, seeds[p] with { Next = seed.Next });
        if (seed.Next is { } n) seeds = seeds.SetItem(n, seeds[n] with { Previous = seed.Previous });
        group = group with
        {
            Seeds = seeds,
            FirstSeed = group.FirstSeed == bin ? seed.Next : group.FirstSeed,
            LastSeed = group.LastSeed == bin ? seed.Previous : group.LastSeed
        };
        return data with { Splits = data.Splits.SetItem(key, group) };
    }

    private RasterEpoch RemoveTessellationGroup(ref RenderGraphEdit graph, RasterEpoch data, TessellationSplitKey key)
    {
        TessellationGroup group = data.Splits[key];
        GraphPassId before = group.Previous is { } previous ? data.Splits[previous].Split.Last : data.Work.Last;
        GraphPassId after = group.Next is { } next ? data.Splits[next].Reset.First : data.Patches.First;
        graph.OrderAfter(after, before);
        RemoveRasterPasses(ref graph, group.Reset);
        RemoveRasterPasses(ref graph, group.Split);
        var groups = data.Splits.Remove(key);
        if (group.Previous is { } p) groups = groups.SetItem(p, groups[p] with { Next = group.Next });
        if (group.Next is { } n) groups = groups.SetItem(n, groups[n] with { Previous = group.Previous });
        data = data with
        {
            Splits = groups,
            FirstSplit = data.FirstSplit == key ? group.Next : data.FirstSplit,
            LastSplit = data.LastSplit == key ? group.Previous : data.LastSplit
        };
        if (groups.Count == 0)
        {
            RemoveRasterPasses(ref graph, data.Patches);
            foreach (GraphPassId consumer in data.Consumers) graph.OrderAfter(consumer, data.Work.Last);
            data = data with { Patches = new RasterPassGroup() };
        }
        return data;
    }

    private void UpdateTessellationSplit(ref RenderGraphEdit graph, RasterEpoch data, TessellationGroup group)
    {
        uint bin = group.FirstSeed!.Value;
        ClusterRasterInvocation stage = data.Bins[bin].Stage;
        Span<ClusterTessellationUniforms> value = stackalloc ClusterTessellationUniforms[1];
        for (int level = 0; level < group.Split.SplitUniforms.Count; level++)
        {
            value[0] = CreateTessellationUniforms(_frameResources,
                group.Seeds.Count > 1 ? uint.MaxValue : bin, data.PhaseOne, (uint)level);
            _uploads!.SetUniformValue(ref graph, group.Split.SplitUniforms[level], MemoryMarshal.AsBytes(value));
        }
        var resources = stage.Tessellation!.SplitResources;
        foreach (GraphPassId pass in group.Split.Passes)
        {
            if (!_materialInputs.TryGetValue(pass, out var inputs)) continue;
            for (int i = 0; i < inputs.Count; i++)
            {
                MaterialInput input = inputs[i];
                int resourceIndex = 0;
                while (resources.Resources.Resources[resourceIndex].Name != input.Name) resourceIndex++;
                MaterialInput updated = input with { Bin = bin, MaterialSlot = resources.MaterialBindings[resourceIndex].MaterialSlot };
                if (input != updated) inputs = inputs.SetItem(i, updated);
            }
            _materialInputs = _materialInputs.SetItem(pass, inputs);
        }
    }

    private readonly RasterBinning?[] _rasterBinning = new RasterBinning?[RasterEpochCount];

    private sealed record RasterBinning(
        GraphBufferInput Uniforms, GraphPassId Reset, GraphPassId Reserve, bool PolicyTarget,
        GraphPassId Count = default, GraphPassId Scatter = default);

    private void UpdateRasterBinning(ref RenderGraphEdit graph)
    {
        Span<ClusterRasterBinningUniforms> uniforms = stackalloc ClusterRasterBinningUniforms[1];
        foreach (RasterBinning? binning in _rasterBinning)
            if (binning is not null)
            {
                uniforms[0] = new ClusterRasterBinningUniforms
                {
                    RasterMaxBins = _frameResources.RasterBinCount,
                    SlotCapacity = _frameResources.SlotCapacity,
                    MaxVisibleClusters = _options.MaxCandidates,
                    CollectMetrics = _options.EnableFrameMetricsReadback ? 1u : 0u,
                    PolicyTarget = binning.PolicyTarget ? 1u : 0u,
                    ScreenWidth = checked((uint)_frameResources.Width),
                    ScreenHeight = checked((uint)_frameResources.Height),
                    ViewCount = _frameResources.ViewCount,
                };
                _uploads!.SetUniformValue(ref graph, binning.Uniforms, MemoryMarshal.AsBytes(uniforms));
            }
        Span<ClusterTessellationBinUniforms> patches = stackalloc ClusterTessellationBinUniforms[1];
        patches[0] = new ClusterTessellationBinUniforms
        {
            BinCount = checked(_frameResources.RasterBinCount * 8u),
            MaxPatchItems = _options.MaxCandidates,
            SourceCounterOffset = 2u * sizeof(uint),
        };
        foreach (RasterEpoch epoch in _rasterEpochs.Values)
            if (epoch.Patches.PatchBinning is { } binning)
                _uploads!.SetUniformValue(ref graph, binning.Uniforms, MemoryMarshal.AsBytes(patches));
    }

    private ulong MeshRasterOffset(uint bin) => checked((ulong)_frameResources.RasterBinCount * 8u *
        ClusterIndirectAbi.DrawIndexedBytes + (ulong)bin * ClusterIndirectAbi.DispatchBytes);

    private void UpdateRasterCapacity(ref RenderGraphEdit graph, uint previousCount)
    {
        if (previousCount == _frameResources.RasterBinCount) return;
        Span<ClusterSoftwareRasterUniforms> software = stackalloc ClusterSoftwareRasterUniforms[1];
        foreach (RasterEpoch epoch in _rasterEpochs.Values)
        {
            foreach (RasterBin bin in epoch.Bins.Values)
            {
                if (bin.Passes.SoftwareUniforms == default) continue;
                software[0] = new ClusterSoftwareRasterUniforms
                {
                    ScreenWidth = checked((uint)_frameResources.Width),
                    ScreenHeight = checked((uint)_frameResources.Height),
                    MaxBins = _frameResources.RasterBinCount,
                    DebugDump = _options.EnableFrameMetricsReadback ? 1u : 0u,
                    CurrentBin = bin.Stage.BinIndex,
                };
                _uploads!.SetUniformValue(ref graph, bin.Passes.SoftwareUniforms, MemoryMarshal.AsBytes(software));
            }
        }
        foreach (MeshRaster mesh in _meshRasterPasses.Values)
            graph.SetBufferAccess(mesh.Access, GraphAccessMode.Read, WriteCoverage.Partial,
                PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument,
                new BufferRange(MeshRasterOffset(mesh.Bin), ClusterIndirectAbi.DispatchBytes));
    }

    private void UpdateRasterDispatches(ref RenderGraphFrame graph)
    {
        foreach ((GraphPassId pass, MeshRaster mesh) in _meshRasterPasses)
            graph.SetPassDataIfChanged(pass, mesh.Data with { IndirectOffset = MeshRasterOffset(mesh.Bin) });
        var reset = new ClusterDispatchParameters(new DispatchArguments(
            Math.Max(1u, (_frameResources.RasterBinCount + 63u) / 64u), 1u, 1u));
        var reserve = new ClusterDispatchParameters(new DispatchArguments(
            Math.Max(1u, (_frameResources.RasterBinCount + 127u) / 128u), 1u, 1u));
        foreach (RasterBinning? binning in _rasterBinning)
        {
            if (binning is null) continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
        }
        uint patchBinCount = checked(_frameResources.RasterBinCount * 8u);
        reset = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (patchBinCount + 63u) / 64u), 1u, 1u));
        reserve = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (patchBinCount + 127u) / 128u), 1u, 1u));
        foreach (RasterEpoch epoch in _rasterEpochs.Values)
        {
            if (epoch.Patches.PatchBinning is not { } binning) continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
        }
    }
}
