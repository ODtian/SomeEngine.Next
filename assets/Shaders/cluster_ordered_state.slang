#ifndef CLUSTER_ORDERED_STATE_SLANG
#define CLUSTER_ORDERED_STATE_SLANG

// Values use the shared RHI CompareOperation, StencilOperation and blend enums.
struct OrderedMaterialState
{
    int DrawOrder;
    uint Enabled, StencilRef, Front;
    uint Back, ReadMask, WriteMask, Flags;
    uint ColorBlend, AlphaBlend, ColorMask, DepthCompare;
    float4 BlendConstant;
};

StructuredBuffer<OrderedMaterialState> OrderedMaterialStates;
RWTexture2DArray<uint> OrderedStencil;

bool OrderedCompare(uint operation, float reference, float value)
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

uint OrderedStencilOperation(uint operation, uint reference, uint value)
{
    switch (operation)
    {
        case 1u: return 0u;
        case 2u: return reference;
        case 3u: return min(255u, value + 1u);
        case 4u: return value > 0u ? value - 1u : 0u;
        case 5u: return value ^ 255u;
        case 6u: return (value + 1u) & 255u;
        case 7u: return (value - 1u) & 255u;
        default: return value;
    }
}

float4 OrderedBlendFactor(uint factor, float4 source, float4 destination, float4 constant)
{
    switch (factor)
    {
        case 0u: return 0.0.xxxx;
        case 1u: return 1.0.xxxx;
        case 2u: return source;
        case 3u: return 1.0 - source;
        case 4u: return source.aaaa;
        case 5u: return 1.0 - source.aaaa;
        case 6u: return destination.aaaa;
        case 7u: return 1.0 - destination.aaaa;
        case 8u: return destination;
        case 9u: return 1.0 - destination;
        case 10u: return float4(min(source.a, 1.0 - destination.a).xxx, 1.0);
        case 11u: return constant;
        case 12u: return 1.0 - constant;
        // A user material exposes one color output; its source-1 factors use that output.
        case 13u: return source;
        case 14u: return 1.0 - source;
        case 15u: return source.aaaa;
        default: return 1.0 - source.aaaa;
    }
}

float4 OrderedBlendOperation(uint operation, float4 source, float4 destination,
    float4 sourceFactor, float4 destinationFactor)
{
    if (operation == 3u) return min(source, destination);
    if (operation == 4u) return max(source, destination);
    float4 a = source * sourceFactor, b = destination * destinationFactor;
    if (operation == 1u) return a - b;
    if (operation == 2u) return b - a;
    return a + b;
}

float4 OrderedBlend(OrderedMaterialState state, float4 source, float4 destination)
{
    float4 result = source;
    if ((state.Flags & 8u) != 0u)
    {
        float4 color = OrderedBlendOperation(state.ColorBlend >> 16u, source, destination,
            OrderedBlendFactor(state.ColorBlend & 255u, source, destination, state.BlendConstant),
            OrderedBlendFactor((state.ColorBlend >> 8u) & 255u, source, destination, state.BlendConstant));
        float4 alpha = OrderedBlendOperation(state.AlphaBlend >> 16u, source, destination,
            OrderedBlendFactor(state.AlphaBlend & 255u, source, destination, state.BlendConstant),
            OrderedBlendFactor((state.AlphaBlend >> 8u) & 255u, source, destination, state.BlendConstant));
        result = float4(color.rgb, alpha.a);
    }
    [unroll]
    for (uint channel = 0u; channel < 4u; channel++)
        if ((state.ColorMask & (1u << channel)) == 0u) result[channel] = destination[channel];
    return result;
}

bool AdvanceOrderedStencil(OrderedMaterialState state, bool front, float depth,
    inout float currentDepth, inout uint stencil)
{
    uint face = front ? state.Front : state.Back;
    bool stencilPass = (state.Flags & 4u) == 0u ||
        OrderedCompare(face & 255u, float(state.StencilRef & state.ReadMask), float(stencil & state.ReadMask));
    bool depthPass = (state.Flags & 1u) == 0u || OrderedCompare(state.DepthCompare, depth, currentDepth);
    uint operation = !stencilPass ? (face >> 8u) & 255u :
        !depthPass ? (face >> 16u) & 255u : face >> 24u;
    if ((state.Flags & 4u) != 0u)
    {
        uint changed = OrderedStencilOperation(operation, state.StencilRef, stencil);
        stencil = (stencil & (255u ^ state.WriteMask)) | (changed & state.WriteMask);
    }
    if (!stencilPass || !depthPass) return false;
    if ((state.Flags & 2u) != 0u) currentDepth = depth;
    return true;
}

#endif
