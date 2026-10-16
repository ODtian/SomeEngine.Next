using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct OrderedMaterialState
    {
        internal int DrawOrder;
        internal uint Enabled, StencilRef, Front;
        internal uint Back, ReadMask, WriteMask, Flags;
        internal uint ColorBlend, AlphaBlend, ColorMask, DepthCompare;
        internal Vector4 BlendConstant;
    }

    private void UpdateOrderedStates(ref RenderGraphEdit edit, TransparencyData data, ClusterMaterialAssignmentSet assignments)
    {
        var values = new OrderedMaterialState[assignments.SlotCapacity];
        foreach (int address in assignments.ActiveOffsets)
            if (assignments.TryGetEntry(address, out Material material, out _) && material.State.Ordered)
            {
                MaterialState state = material.State;
                DepthStencilState tests = state.DepthStencil;
                BlendAttachmentState blend = state.Blend;
                values[address] = new()
                {
                    DrawOrder = state.DrawOrder, Enabled = 1, StencilRef = state.StencilRef,
                    Front = Face(tests.Front), Back = Face(tests.Back),
                    ReadMask = tests.StencilReadMask, WriteMask = tests.StencilWriteMask,
                    Flags = (tests.DepthTest ? 1u : 0u) | (tests.DepthWrite ? 2u : 0u) |
                        (tests.StencilTest ? 4u : 0u) | (blend.Enabled ? 8u : 0u),
                    ColorBlend = Blend(blend.SourceColor, blend.DestinationColor, blend.ColorOperation),
                    AlphaBlend = Blend(blend.SourceAlpha, blend.DestinationAlpha, blend.AlphaOperation),
                    ColorMask = (uint)blend.WriteMask, DepthCompare = (uint)tests.DepthComparison,
                    BlendConstant = state.BlendConstant,
                };
            }
        byte[] bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        UpdateBufferData(ref edit, data.OrderedStates, ClusterBytes.FromBytes(bytes), [new BufferRange(0, (ulong)bytes.Length)]);

        static uint Face(StencilFaceState face) => (uint)face.Comparison | (uint)face.Fail << 8 |
            (uint)face.DepthFail << 16 | (uint)face.Pass << 24;
        static uint Blend(BlendFactor source, BlendFactor destination, BlendOperation operation) =>
            (uint)source | (uint)destination << 8 | (uint)operation << 16;
    }
}
