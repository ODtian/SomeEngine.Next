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
    private GraphBufferId _shadeBinUniforms;
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
        var previousReaders = _pageHeapReaderPasses;
        FrameResources previousResources = _frameResources;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            _uploads!.BeginDeclarations();
            UpdateScalarBuffers(ref edit, scalars);
            ReconcileStageResources(ref edit, assignments, rasterStages, shadeStages);
            UpdateShadeBinning(ref edit);
            UpdateShadeGroups(ref edit, shadeStages);
            var materialBuffers = UpdateMaterialBuffers(ref edit, assignments);
            edit.Commit();
            _materialBuffers = materialBuffers;
        }
        catch
        {
            _uploads!.CancelDeclarations();
            _shadeGroups = previousGroups;
            _materialInputs = previousInputs;
            _pageHeapReaderPasses = previousReaders;
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
                if (group.Stage.Pipeline == stage.Pipeline &&
                    group.Stage.CommandBindings.Equals(stage.CommandBindings) &&
                    group.Stage.MaterialBindings.AsSpan().SequenceEqual(stage.MaterialBindings))
                    continue;
            }
            GraphPassId before = ShadePredecessor(bin);
            GraphPassId after = ShadeSuccessor(bin);
            graph.Remove(group.Pass);
            if (after != default) graph.OrderAfter(after, before);
            _shadeGroups = _shadeGroups.Remove(bin);
            _shadeGroupOrder.Remove(bin);
            _materialInputs = _materialInputs.Remove(group.Pass);
            _pageHeapReaderPasses = _pageHeapReaderPasses.Remove(group.Pass);
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
                dispatch, default, stage.Pipeline.Pipeline, ShaderVariableLayout.Null, default,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record);
            GraphBufferInput uniforms = BindShadeResources(ref graph, pass, stage);
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
    }

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

    private void UpdateShadeBinning(ref RenderGraphEdit graph)
    {
        Span<ClusterShadeBinUniforms> value = stackalloc ClusterShadeBinUniforms[1];
        value[0] = new ClusterShadeBinUniforms
        {
            ScreenWidth = checked((uint)_frameResources.Width),
            ScreenHeight = checked((uint)_frameResources.Height),
            ShadeBinCount = _frameResources.ShadeBinCount,
            SlotCapacity = _frameResources.SlotCapacity,
        };
        _uploads!.SetValue(ref graph, _shadeBinUniforms, MemoryMarshal.AsBytes(value));
    }
}
