#ifndef LAYER_OPERATIONS_SLANG
#define LAYER_OPERATIONS_SLANG

__exported import layer_fragment_types;

struct LayerOperation
{
    uint Color, Opaque, DepthTestTarget, DepthWriteTarget;
    uint DepthComparison, StencilTest, StencilReadMask, StencilWriteMask;
    uint FrontFail, FrontDepthFail, FrontPass, FrontComparison;
    uint BackFail, BackDepthFail, BackPass, BackComparison;
    uint XRay, ColorWriteMask, Padding1, Padding2;
    float4 VisibleColor;
    float4 OccludedColor;
};

// Front-to-back colors represent F(x) = clamp(Contribution + Factor*x, Minimum, Maximum).
struct LayerColorAccumulator
{
    float4 Color;
    float3 Contribution;
    float3 Factor;
    float3 Minimum;
    float3 Maximum;
    float Depth;
    float OpaqueDepth;
    uint Stencil;
    uint DrawOrder;
    bool HasDrawOrder;
    bool Finished;
};

LayerColorAccumulator BeginLayerColorAccumulator(float4 color, float depth, float opaqueDepth, uint stencil)
{
    LayerColorAccumulator result = {};
    result.Color = color;
    result.Factor = 1.0;
    result.Minimum = -3.402823466e38;
    result.Maximum = 3.402823466e38;
    result.Depth = depth;
    result.OpaqueDepth = opaqueDepth;
    result.Stencil = stencil;
    return result;
}

bool LayerCompare(uint operation, float reference, float value)
{
    switch (operation)
    {
        case 0u: return false;
        case 1u: return reference < value;
        case 2u: return reference == value;
        case 3u: return reference <= value;
        case 4u: return reference > value;
        case 5u: return reference != value;
        case 6u: return reference >= value;
        default: return true;
    }
}

uint LayerStencilChange(uint operation, uint reference, uint value)
{
    switch (operation)
    {
        case 1u: return 0u;
        case 2u: return reference;
        case 3u: return min(value + 1u, 255u);
        case 4u: return value == 0u ? 0u : value - 1u;
        case 5u: return value ^ 255u;
        case 6u: return (value + 1u) & 255u;
        case 7u: return (value - 1u) & 255u;
        default: return value;
    }
}

bool LayerApplyDepthStencil(LayerOperation operation, LayerFragment fragment, uint reference,
    inout LayerColorAccumulator accumulator)
{
    bool front = LayerFragmentIsFront(fragment);
    uint comparison = front ? operation.FrontComparison : operation.BackComparison;
    bool stencilPass = operation.StencilTest == 0u || LayerCompare(comparison,
        float(reference & operation.StencilReadMask), float(accumulator.Stencil & operation.StencilReadMask));
    float storedDepth = operation.DepthTestTarget == 1u ? accumulator.Depth : accumulator.OpaqueDepth;
    bool depthPass = operation.DepthTestTarget == 0u || LayerCompare(operation.DepthComparison, fragment.Depth, storedDepth);
    if (operation.StencilTest != 0u)
    {
        uint change = !stencilPass ? (front ? operation.FrontFail : operation.BackFail) :
            !depthPass ? (front ? operation.FrontDepthFail : operation.BackDepthFail) :
            (front ? operation.FrontPass : operation.BackPass);
        uint changed = LayerStencilChange(change, reference, accumulator.Stencil);
        accumulator.Stencil = (accumulator.Stencil & (255u ^ operation.StencilWriteMask)) |
            (changed & operation.StencilWriteMask);
    }
    if (!stencilPass || !depthPass) return false;
    if (operation.DepthWriteTarget == 1u) accumulator.Depth = fragment.Depth;
    if (operation.DepthWriteTarget == 2u) accumulator.OpaqueDepth = fragment.Depth;
    return true;
}

void LayerApplyColor(LayerOperation operation, LayerColor color, float depth, uint sortMode,
    inout LayerColorAccumulator accumulator)
{
    LayerColorAccumulator previous = accumulator;
    float3 source = LayerColorPremultiplied(color);
    float3 transmission = LayerColorTransmittance(color);
    if (operation.Color == 0u) transmission = transmission.xxx;
    if (operation.XRay != 0u)
    {
        float4 multiplier = depth <= accumulator.OpaqueDepth ? operation.VisibleColor : operation.OccludedColor;
        source *= multiplier.rgb * multiplier.a;
        transmission = 1.0 - (1.0 - transmission) * multiplier.a;
    }
    bool frontToBack = sortMode != 2u;
    switch (operation.Color)
    {
        case 0u:
        case 1u:
            if (frontToBack) accumulator.Contribution += accumulator.Factor * source;
            else accumulator.Color.rgb = source + transmission * accumulator.Color.rgb;
            accumulator.Factor *= transmission;
            break;
        case 2u:
            if (frontToBack) accumulator.Contribution += accumulator.Factor * source;
            else accumulator.Color.rgb += source;
            break;
        case 3u:
            if (frontToBack) accumulator.Factor *= source + transmission;
            else accumulator.Color.rgb *= source + transmission;
            break;
        case 4u:
            if (frontToBack)
            {
                float3 bound = clamp(accumulator.Contribution + accumulator.Factor * source,
                    accumulator.Minimum, accumulator.Maximum);
                accumulator.Maximum = select(accumulator.Factor >= 0.0, min(accumulator.Maximum, bound), accumulator.Maximum);
                accumulator.Minimum = select(accumulator.Factor < 0.0, max(accumulator.Minimum, bound), accumulator.Minimum);
            }
            else accumulator.Color.rgb = min(accumulator.Color.rgb, source);
            break;
        case 5u:
            if (frontToBack)
            {
                float3 bound = clamp(accumulator.Contribution + accumulator.Factor * source,
                    accumulator.Minimum, accumulator.Maximum);
                accumulator.Minimum = select(accumulator.Factor >= 0.0, max(accumulator.Minimum, bound), accumulator.Minimum);
                accumulator.Maximum = select(accumulator.Factor < 0.0, min(accumulator.Maximum, bound), accumulator.Maximum);
            }
            else accumulator.Color.rgb = max(accumulator.Color.rgb, source);
            break;
    }
    if (frontToBack && operation.Opaque != 0u) accumulator.Factor = 0.0;
    bool3 writes = (operation.ColorWriteMask & uint3(1u, 2u, 4u)) != 0u;
    accumulator.Color.rgb = select(writes, accumulator.Color.rgb, previous.Color.rgb);
    accumulator.Contribution = select(writes, accumulator.Contribution, previous.Contribution);
    accumulator.Factor = select(writes, accumulator.Factor, previous.Factor);
    accumulator.Minimum = select(writes, accumulator.Minimum, previous.Minimum);
    accumulator.Maximum = select(writes, accumulator.Maximum, previous.Maximum);
    if (frontToBack && all(accumulator.Factor == 0.0)) accumulator.Finished = true;
}

float4 ResolveLayerColor(LayerColorAccumulator accumulator, uint sortMode)
{
    return sortMode != 2u ? float4(clamp(accumulator.Contribution + accumulator.Factor * accumulator.Color.rgb,
        accumulator.Minimum, accumulator.Maximum), accumulator.Color.a) : accumulator.Color;
}

void LayerBeginDrawOrder(uint sortMode, uint drawOrder, inout LayerColorAccumulator accumulator)
{
    if (sortMode != 1u) return;
    if (accumulator.HasDrawOrder && accumulator.DrawOrder != drawOrder)
    {
        accumulator.Color = ResolveLayerColor(accumulator, sortMode);
        accumulator.Contribution = 0.0;
        accumulator.Factor = 1.0;
        accumulator.Minimum = -3.402823466e38;
        accumulator.Maximum = 3.402823466e38;
        accumulator.Finished = false;
    }
    accumulator.DrawOrder = drawOrder;
    accumulator.HasDrawOrder = true;
}

#endif
