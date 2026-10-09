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
        var reads = new Dictionary<uint, HashSet<IndexedTextureRead>>();
        Span<byte> binBytes = stackalloc byte[sizeof(uint)];
        foreach (int address in _materialAssignments.Current.ActiveOffsets)
        {
            if (!stages.ScalarLayouts.TryGetValue(address, out var scalarLayout)) continue;
            _materialAssignments.Current.TryGetEntry(address, out var material, out _);
            stages.BinIndices.CopyTo(binBytes, address * sizeof(uint));
            uint bin = BinaryPrimitives.ReadUInt32LittleEndian(binBytes);
            foreach (uint property in scalarLayout.TextureProperties)
            {
                if (!reads.TryGetValue(bin, out var members)) reads.Add(bin, members = []);
                members.Add(new(material, property, material.GetTexture(property)));
            }
        }
        var desired = stages.Invocations.ToDictionary(static stage => stage.BinIndex,
            stage => new ShadeGroup(receivers ? stage.SurfaceShader! : stage,
                reads.TryGetValue(stage.BinIndex, out var members)
                    ? members.OrderBy(static value => value.Material.MaterialId).ThenBy(static value => value.Property).ToImmutableArray()
                    : []));
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
                value.Uniforms = BindShadeResources(ref edit, pass, value.Stage, stages);
                PassDefinition access = edit.GetPass(pass);
                foreach (IndexedTextureRead read in value.TextureReads)
                {
                    MaterialInput input = GetMaterialInput(ref edit, read.Material, read.Property,
                        read.Material.Properties[checked((int)read.Property)].Name, ResourceBindingType.TextureSrv);
                    access.Read(input.Input, PipelineSync.ComputeShading);
                }
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

    private void UpdateShadeBinning(ref RenderGraphEdit graph)
    {
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
