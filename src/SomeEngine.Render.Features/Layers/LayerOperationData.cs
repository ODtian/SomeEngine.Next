using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Resources.Layers;

namespace SomeEngine.Render.Features.Layers;

/// <summary>One row of the engine's composition-operation buffer.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 112)]
public readonly struct LayerOperationData
{
    public readonly uint Color, Opaque, DepthTestTarget, DepthWriteTarget;
    public readonly uint DepthComparison, StencilTest, StencilReadMask, StencilWriteMask;
    public readonly uint FrontFail, FrontDepthFail, FrontPass, FrontComparison;
    public readonly uint BackFail, BackDepthFail, BackPass, BackComparison;
    public readonly uint XRay, ColorWriteMask, Padding1, Padding2;
    public readonly Vector4 VisibleColor, OccludedColor;

    public LayerOperationData(in LayerOperation value)
    {
        this = default;
        Color = (uint)value.Color;
        Opaque = value.Opaque ? 1u : 0u;
        DepthTestTarget = (uint)value.DepthTestTarget;
        DepthWriteTarget = (uint)value.DepthWriteTarget;
        DepthComparison = (uint)value.DepthComparison;
        StencilTest = value.StencilTest ? 1u : 0u;
        StencilReadMask = value.StencilReadMask;
        StencilWriteMask = value.StencilWriteMask;
        FrontFail = (uint)value.Front.Fail;
        FrontDepthFail = (uint)value.Front.DepthFail;
        FrontPass = (uint)value.Front.Pass;
        FrontComparison = (uint)value.Front.Comparison;
        BackFail = (uint)value.Back.Fail;
        BackDepthFail = (uint)value.Back.DepthFail;
        BackPass = (uint)value.Back.Pass;
        BackComparison = (uint)value.Back.Comparison;
        XRay = value.XRay ? 1u : 0u;
        ColorWriteMask = (uint)value.ColorWriteMask;
        VisibleColor = value.VisibleColor;
        OccludedColor = value.OccludedColor;
    }
}

/// <summary>Operation and stencil values indexed by the renderer's global material address.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public readonly record struct LayerMaterialComposition(uint OperationIndex, uint StencilReference = 0);

/// <summary>Visibility-pass selection indexed by global material address.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public readonly record struct LayerRasterMaterial(uint PassIndex, uint DrawOrderAndFlags);
