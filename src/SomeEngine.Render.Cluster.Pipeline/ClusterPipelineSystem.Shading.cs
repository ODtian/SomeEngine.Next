using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private static uint[] GetSurfaceDataSizes(ClusterShadeStageSet stages)
    {
        uint[] sizes = new uint[Math.Max(1u, stages.BinCount)];
        foreach (ClusterShadeInvocation stage in stages.Invocations)
            sizes[stage.BinIndex] = stage.SurfaceDataSize;
        return sizes;
    }

    private readonly record struct IndexedTextureRead(SomeEngine.Resources.Material Material, uint Property,
        SomeEngine.Resources.Texture? Texture);

    private sealed record ShadeGroup(ClusterShadeInvocation Stage, ImmutableArray<IndexedTextureRead> TextureReads)
    {
        internal GraphBufferSlice Uniforms;
    }

    private sealed class ShadeGroupComparer : IEqualityComparer<ShadeGroup>
    {
        internal static readonly ShadeGroupComparer Instance = new();
        public bool Equals(ShadeGroup? left, ShadeGroup? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null &&
            ReferenceEquals(left.Stage.Material, right.Stage.Material) &&
            left.Stage.Pipeline == right.Stage.Pipeline &&
            left.Stage.CommandBindings.Equals(right.Stage.CommandBindings) &&
            left.Stage.MaterialBindings.AsSpan().SequenceEqual(right.Stage.MaterialBindings) &&
            left.TextureReads.AsSpan().SequenceEqual(right.TextureReads.AsSpan());
        public int GetHashCode(ShadeGroup value) => value.Stage.BinIndex.GetHashCode();
    }

    private GraphRegionMap<uint, ShadeGroup> UpdateShadeGroups(ref RenderGraphEdit graph, ClusterShadeStageSet stages)
    {
        GraphData.ReceiverGroups = UpdateReceiverGroups(ref graph, stages);
        return UpdateMaterialGroups(ref graph, stages, GraphData.ShadeGroups, receivers: false);
    }

    private GraphRegionMap<uint, ShadeGroup> UpdateReceiverGroups(ref RenderGraphEdit graph, ClusterShadeStageSet stages)
        => UpdateMaterialGroups(ref graph, stages, GraphData.ReceiverGroups, receivers: true);

    private GraphRegionMap<uint, ShadeGroup> UpdateMaterialGroups(ref RenderGraphEdit graph, ClusterShadeStageSet stages,
        GraphRegionMap<uint, ShadeGroup> previousGroups, bool receivers)
    {
        var reads = CollectIndexedTextureReads(stages.BinIndices, stages);
        var desired = stages.Invocations.Where(stage => !receivers || stage.SurfaceShader is not null).ToDictionary(static stage => stage.BinIndex,
            stage => new ShadeGroup(receivers ? stage.SurfaceShader! : stage,
                reads.GetValueOrDefault(stage.BinIndex, [])));
        foreach (ShadeGroup group in previousGroups.Values)
            if (!desired.TryGetValue(group.Stage.BinIndex, out ShadeGroup? next) ||
                !ShadeGroupComparer.Instance.Equals(group, next))
                RemoveParameters(group.Uniforms);
        GraphRegionMap<uint, ShadeGroup> groups = previousGroups.Reconcile(ref graph, desired,
            (ref RenderGraphEdit edit, GraphRegionId region, uint bin, ShadeGroup value) =>
            {
                var dispatch = new ClusterIndirectDispatchParameters(RequireDispatchIndirectLayout(),
                    GraphData.Resources.ShadeIndirectArgs, checked((ulong)bin * ClusterIndirectAbi.DispatchBytes));
                GraphPassId pass = edit.AddComputeFramePass($"Evaluate Cluster {(receivers ? "receivers" : "color")} bin {bin}",
                    PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch, new PassOptions(RepeatCommands: true),
                    value.Stage.Pipeline.Pipeline, VariableLayoutReflection.Null, default,
                    static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                        _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                            PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                    ClusterIndirectDispatchParameters.Record, region: region);
                value.Uniforms = BindShadeResources(ref edit, pass, value.Stage, stages, receivers);
                PassDefinition access = edit.GetPass(pass);
                ReadIndexedMaterialTextures(ref edit, ref access, value.TextureReads, PipelineSync.ComputeShading);
            });
        if (_installedShadeStages is { } previous &&
            (previous.BinCount != stages.BinCount || previous.BinIndices.Length != stages.BinIndices.Length ||
                previous.SurfaceDataStride != stages.SurfaceDataStride))
        {
            Span<ClusterShadeUniforms> value = stackalloc ClusterShadeUniforms[1];
            foreach (ShadeGroup group in groups.Values)
            {
                value[0] = ShadeUniforms(group.Stage.BinIndex, stages);
                UpdateParameters(ref graph, group.Uniforms, MemoryMarshal.AsBytes(value));
            }
        }
        return groups;
    }

    private Dictionary<uint, ImmutableArray<IndexedTextureRead>> CollectIndexedTextureReads(
        ClusterBytes binIndices, ClusterShadeStageSet stages, uint binMask = uint.MaxValue)
    {
        var reads = new Dictionary<uint, HashSet<IndexedTextureRead>>();
        Span<byte> binBytes = stackalloc byte[sizeof(uint)];
        foreach (int address in _materialAssignments.Current.ActiveOffsets)
        {
            if (!stages.ScalarLayouts.TryGetValue(address, out var scalarLayout)) continue;
            _materialAssignments.Current.TryGetEntry(address, out var material, out _);
            binIndices.CopyTo(binBytes, address * sizeof(uint));
            uint bin = BinaryPrimitives.ReadUInt32LittleEndian(binBytes) & binMask;
            foreach (uint property in scalarLayout.TextureProperties)
            {
                if (!reads.TryGetValue(bin, out var members)) reads.Add(bin, members = []);
                members.Add(new(material, property, material.GetTexture(property)));
            }
        }
        return reads.ToDictionary(static pair => pair.Key, static pair => pair.Value
            .OrderBy(static value => value.Material.MaterialId).ThenBy(static value => value.Property).ToImmutableArray());
    }

    private void ReadIndexedMaterialTextures(ref RenderGraphEdit graph, ref PassDefinition pass,
        ImmutableArray<IndexedTextureRead> reads, PipelineSync sync)
    {
        foreach (IndexedTextureRead read in reads)
        {
            MaterialInput input = GetMaterialInput(ref graph, read.Material, read.Property,
                read.Material.Properties[checked((int)read.Property)].Name, ResourceBindingType.TextureSrv);
            pass.Read(input.Input, sync);
        }
    }

    private ClusterShadeUniforms ShadeUniforms(uint bin, ClusterShadeStageSet stages) => new()
    {
        ScreenWidth = checked((uint)GraphData.Resources.Width),
        ScreenHeight = checked((uint)GraphData.Resources.Height),
        ShadingBin = bin,
        ShadeBinCount = stages.BinCount,
        MaterialSlotCapacity = checked((uint)(stages.BinIndices.Length / sizeof(uint))),
        ViewCount = GraphData.Resources.ViewCount,
        SurfaceDataStride = stages.GetBin(bin).SurfaceDataSize,
        VisibilityDepth = (uint)stages.GetBin(bin).VisibilityDepth,
    };

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

    private void UpdateShadeBinning(ref RenderGraphEdit graph, ClusterShadeStageSet stages)
    {
        if (_installedShadeStages is { } previous && previous.WritesReceivers != stages.WritesReceivers)
        {
            GraphPassId previousPass = GraphData.ShadeCountPass;
            graph.Remove(previousPass);
            GraphData.LayerDispatches.RemoveAll(dispatch => dispatch.Pass == previousPass);
            GraphData.ShadeCountPass = RecordShadeCount(ref graph, GraphData.Resources,
                GraphData.ShadeBinUniforms, stages.WritesReceivers);
        }
        Span<ClusterShadeBinUniforms> value = stackalloc ClusterShadeBinUniforms[1];
        value[0] = new ClusterShadeBinUniforms
        {
            ScreenWidth = checked((uint)GraphData.Resources.Width),
            ScreenHeight = checked((uint)GraphData.Resources.Height),
            ShadeBinCount = GraphData.Resources.ShadeBinCount,
            SlotCapacity = GraphData.Resources.SlotCapacity,
            BinItemCapacity = CalculateShadeBinItemCapacity(GraphData.Resources),
            AdditionalReceiverLayerCount = GraphData.Resources.AdditionalReceiverLayerCount,
            ViewCount = GraphData.Resources.ViewCount,
            SurfaceDataStride = GraphData.Resources.SurfaceDataStride,
        };
        UpdateParameters(ref graph, GraphData.ShadeBinUniforms, MemoryMarshal.AsBytes(value));
    }
}
