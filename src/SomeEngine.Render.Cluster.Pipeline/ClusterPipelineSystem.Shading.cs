using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private System.Collections.Immutable.ImmutableDictionary<uint, ShadeGroup> _shadeGroups =
        System.Collections.Immutable.ImmutableDictionary<uint, ShadeGroup>.Empty;
    private readonly SortedSet<uint> _shadeGroupOrder = [];
    private GraphBufferInput _shadeBinUniforms = null!;
    private GraphPassId _shadeCountPass;
    private GraphPassId _shadeScatterPass;
    private GraphPassId _shadeClearPass;
    private GraphPassId _shadeReservePass;

    private sealed record ShadeGroup(
        GraphPassId Pass, GraphBufferInput Uniforms, ClusterShadeInvocation Stage);

    private void UpdateShading(ClusterMaterialAssignmentSet assignments,
        ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages, ClusterMaterialScalars scalars)
    {
        var previousGroups = _shadeGroups;
        var previousInputs = _materialInputs;
        FrameResources previousResources = _frameResources;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            _uploads!.BeginDeclarations();
            UpdateScalarBuffers(ref edit, scalars);
            ReconcileStageResources(ref edit, assignments, rasterStages, shadeStages);
            UpdateShadeBinning(ref edit);
            UpdateShadeGroups(ref edit, shadeStages);
            var materialResources = UpdateMaterialResources(ref edit, rasterStages, shadeStages);
            edit.Commit();
            _materialBuffers = materialResources.Buffers;
            AcceptMaterialTextures(materialResources.Textures);
        }
        catch
        {
            _uploads!.CancelDeclarations();
            _shadeGroups = previousGroups;
            _materialInputs = previousInputs;
            _frameResources = previousResources;
            foreach (uint bin in shadeStages.ChangedBins)
                if (_shadeGroups.ContainsKey(bin)) _shadeGroupOrder.Add(bin);
                else _shadeGroupOrder.Remove(bin);
            throw;
        }
        finally { edit.Dispose(); }
        _uploads!.CompleteDeclarations(_graph);
    }

    private void UpdateShadeGroups(ref RenderGraphEdit graph, ClusterShadeStageSet stages)
    {
        foreach (uint bin in stages.ChangedBins)
        {
            if (!_shadeGroups.TryGetValue(bin, out ShadeGroup? group)) continue;
            if (stages.TryGetBin(bin, out ClusterShadeInvocation stage))
            {
                if (ReferenceEquals(group.Stage.Material, stage.Material) && group.Stage.Pipeline == stage.Pipeline &&
                    group.Stage.CommandBindings.Equals(stage.CommandBindings) &&
                    group.Stage.MaterialBindings.AsSpan().SequenceEqual(stage.MaterialBindings))
                    continue;
            }
            GraphPassId before = ShadePredecessor(bin);
            GraphPassId after = ShadeSuccessor(bin);
            graph.Remove(group.Pass);
            _uploads!.RemoveUniform(group.Uniforms);
            if (after != default) graph.OrderAfter(after, before);
            _shadeGroups = _shadeGroups.Remove(bin);
            _shadeGroupOrder.Remove(bin);
        }
        foreach (uint bin in stages.ChangedBins)
        {
            if (_shadeGroups.ContainsKey(bin) || !stages.TryGetBin(bin, out ClusterShadeInvocation stage))
                continue;
            var dispatch = new ClusterIndirectDispatchParameters(
                RequireDispatchIndirectLayout(), _frameResources.ShadeIndirectArgs,
                checked((ulong)bin * ClusterIndirectAbi.DispatchBytes));
            GraphPassId pass = graph.AddComputeFramePass(
                $"Shade Cluster bin {bin}", PassQueueSelection.AnyOfType(QueueType.Graphics),
                dispatch, new PassOptions(RepeatCommands: true), stage.Pipeline.Pipeline, VariableLayoutReflection.Null, default,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record);
            GraphBufferInput uniforms = BindShadeResources(ref graph, pass, stage, stages);
            _shadeGroups = _shadeGroups.Add(bin, new ShadeGroup(pass, uniforms, stage));
            _shadeGroupOrder.Add(bin);
            GraphPassId before = ShadePredecessor(bin);
            GraphPassId after = ShadeSuccessor(bin);
            graph.OrderAfter(pass, before);
            if (after != default)
            {
                graph.RemoveOrder(after, before);
                graph.OrderAfter(after, pass);
            }
        }

        // These counts are read by every surviving shade group, not just changed bins.
        // The output dimensions are fixed for this pipeline. A count change therefore
        // expands the affected set to all groups; otherwise their stored values remain valid.
        if (_installedShadeStages is { } previous &&
            (previous.BinCount != stages.BinCount || previous.BinIndices.Length != stages.BinIndices.Length))
        {
            Span<ClusterShadeUniforms> value = stackalloc ClusterShadeUniforms[1];
            foreach ((uint bin, ShadeGroup group) in _shadeGroups)
            {
                value[0] = ShadeUniforms(bin, stages);
                _uploads!.SetUniformValue(ref graph, group.Uniforms, MemoryMarshal.AsBytes(value));
            }
        }
    }

    private ClusterShadeUniforms ShadeUniforms(uint bin, ClusterShadeStageSet stages) => new()
    {
        ScreenWidth = checked((uint)_frameResources.Width),
        ScreenHeight = checked((uint)_frameResources.Height),
        ShadingBin = bin,
        ShadeBinCount = stages.BinCount,
        MaterialSlotCapacity = checked((uint)(stages.BinIndices.Length / sizeof(uint))),
    };

    private GraphPassId ShadePredecessor(uint bin)
    {
        if (bin == 0) return _shadeResolvePass;
        using var before = _shadeGroupOrder.GetViewBetween(0, bin - 1).Reverse().GetEnumerator();
        return before.MoveNext() ? _shadeGroups[before.Current].Pass : _shadeResolvePass;
    }

    private GraphPassId ShadeSuccessor(uint bin)
    {
        if (bin == uint.MaxValue) return _shadeCompleteBefore;
        using var after = _shadeGroupOrder.GetViewBetween(bin + 1, uint.MaxValue).GetEnumerator();
        return after.MoveNext() ? _shadeGroups[after.Current].Pass : _shadeCompleteBefore;
    }

    private static uint CalculateShadeBinItemCapacity(in FrameResources frame)
    {
        ulong itemCapacity = Math.Min((ulong)frame.Width * (ulong)frame.Height * frame.ViewCount, uint.MaxValue);
        ulong quadWidth = ((ulong)frame.Width + 1u) / 2u;
        ulong quadHeight = ((ulong)frame.Height + 1u) / 2u;
        ulong quads = quadWidth * quadHeight * frame.ViewCount;
        // Reserve a disjoint worst-case range per bin only when it fits the existing
        // pixel buffer. Larger bin sets retain compact count/reserve/scatter storage.
        return quads != 0 && quads <= uint.MaxValue && frame.ShadeBinCount <= itemCapacity / quads
            ? (uint)quads : 0u;
    }

    private void UpdateShadeBinning(ref RenderGraphEdit graph)
    {
        Span<ClusterShadeBinUniforms> value = stackalloc ClusterShadeBinUniforms[1];
        value[0] = new ClusterShadeBinUniforms
        {
            ScreenWidth = checked((uint)_frameResources.Width),
            ScreenHeight = checked((uint)_frameResources.Height),
            ShadeBinCount = _frameResources.ShadeBinCount,
            SlotCapacity = _frameResources.SlotCapacity,
            BinItemCapacity = CalculateShadeBinItemCapacity(_frameResources),
        };
        _uploads!.SetUniformValue(ref graph, _shadeBinUniforms, MemoryMarshal.AsBytes(value));
    }
}
