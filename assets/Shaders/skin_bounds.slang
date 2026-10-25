struct SkinBoundsBox { float3 center; uint joint; float3 extent; uint padding; };
struct SkinBoundsControl
{
    uint joints;
    uint inverseBind;
    uint output;
    uint update;
    uint jointCount;
    uint padding0;
    uint padding1;
    uint padding2;
};
struct SkinBoundsTransform
{
    float4 currentX;
    float4 currentY;
    float4 currentZ;
    float4 previousX;
    float4 previousY;
    float4 previousZ;
    float2 stretch;
    float2 padding;
};
struct SkinBoundsGroup
{
    uint instance;
    uint firstBox;
    uint boxCount;
    uint firstUse;
    uint useCount;
    uint padding0;
    uint padding1;
    uint padding2;
};
struct SkinBoundsUse
{
    uint output;
    float error;
    uint flags;
    uint padding;
};

StructuredBuffer<float4x4> SkinCurrentBones;
StructuredBuffer<float4x4> SkinPreviousBones;
StructuredBuffer<uint> SkinJointIndices;
StructuredBuffer<float4x4> SkinInverseBind;
StructuredBuffer<SkinBoundsControl> SkinBoundsControls;
StructuredBuffer<SkinBoundsGroup> SkinBoundsWork;
StructuredBuffer<SkinBoundsUse> SkinBoundsUses;
StructuredBuffer<SkinBoundsBox> SkinBoundsBoxes;
RWStructuredBuffer<SkinBoundsTransform> SkinJointTransforms;
RWByteAddressBuffer SkinCurrentBounds;
RWByteAddressBuffer SkinPreviousBounds;

void IncludeBox(SkinBoundsBox box, float4 x, float4 y, float4 z, inout float3 minimum, inout float3 maximum)
{
    float4 center = float4(box.center, 1.0);
    float3 transformed = float3(dot(center, x), dot(center, y), dot(center, z));
    float3 radius = float3(dot(abs(x.xyz), box.extent), dot(abs(y.xyz), box.extent), dot(abs(z.xyz), box.extent));
    minimum = min(minimum, transformed - radius);
    maximum = max(maximum, transformed + radius);
}

float SkinTransformStretch(float4x4 transform)
{
    float3 first = abs(transform[0].xyz), second = abs(transform[1].xyz), third = abs(transform[2].xyz);
    float frobeniusSquared = dot(first, first) + dot(second, second) + dot(third, third);
    float maximumRow = max(dot(first, 1.0.xxx), max(dot(second, 1.0.xxx), dot(third, 1.0.xxx)));
    float3 columns = first + second + third;
    float maximumColumn = max(columns.x, max(columns.y, columns.z));
    return sqrt(min(frobeniusSquared, maximumRow * maximumColumn));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void TransformSkinJoints(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    SkinBoundsControls.GetDimensions(count, stride);
    if (thread.y >= count) return;
    SkinBoundsControl control = SkinBoundsControls[thread.y];
    if (control.update == 0u || thread.x >= control.jointCount) return;
    uint joint = control.joints + thread.x;
    uint bone = SkinJointIndices[joint];
    float4x4 inverseBind = SkinInverseBind[control.inverseBind + thread.x];
    float4x4 current = mul(inverseBind, SkinCurrentBones[bone]);
    float4x4 previous = mul(inverseBind, SkinPreviousBones[bone]);
    SkinBoundsTransform result;
    result.currentX = float4(current[0].x, current[1].x, current[2].x, current[3].x);
    result.currentY = float4(current[0].y, current[1].y, current[2].y, current[3].y);
    result.currentZ = float4(current[0].z, current[1].z, current[2].z, current[3].z);
    result.previousX = float4(previous[0].x, previous[1].x, previous[2].x, previous[3].x);
    result.previousY = float4(previous[0].y, previous[1].y, previous[2].y, previous[3].y);
    result.previousZ = float4(previous[0].z, previous[1].z, previous[2].z, previous[3].z);
    result.stretch = float2(SkinTransformStretch(current), SkinTransformStretch(previous));
    result.padding = 0.0.xx;
    SkinJointTransforms[joint] = result;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void TransformSkinBounds(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    SkinBoundsWork.GetDimensions(count, stride);
    uint group = thread.x / 4u;
    uint lane = WaveGetLaneIndex() & 3u;
    if (group >= count) return;
    SkinBoundsGroup work = SkinBoundsWork[group];
    SkinBoundsControl control = SkinBoundsControls[work.instance];
    if (control.update == 0u) return;
    float3 currentMin = 1e30.xxx, currentMax = -1e30.xxx;
    float3 previousMin = 1e30.xxx, previousMax = -1e30.xxx;
    float currentStretch = 0.0, previousStretch = 0.0;
    for (uint index = lane; index < work.boxCount; index += 4u)
    {
        SkinBoundsBox box = SkinBoundsBoxes[work.firstBox + index];
        SkinBoundsTransform transform = SkinJointTransforms[control.joints + box.joint];
        IncludeBox(box, transform.currentX, transform.currentY, transform.currentZ, currentMin, currentMax);
        IncludeBox(box, transform.previousX, transform.previousY, transform.previousZ, previousMin, previousMax);
        currentStretch = max(currentStretch, transform.stretch.x);
        previousStretch = max(previousStretch, transform.stretch.y);
    }
    [unroll]
    for (uint width = 1u; width < 4u; width *= 2u)
    {
        uint other = WaveGetLaneIndex() ^ width;
        currentMin = min(currentMin, WaveReadLaneAt(currentMin, other));
        currentMax = max(currentMax, WaveReadLaneAt(currentMax, other));
        previousMin = min(previousMin, WaveReadLaneAt(previousMin, other));
        previousMax = max(previousMax, WaveReadLaneAt(previousMax, other));
        currentStretch = max(currentStretch, WaveReadLaneAt(currentStretch, other));
        previousStretch = max(previousStretch, WaveReadLaneAt(previousStretch, other));
    }
    float4 currentSphere = float4((currentMin + currentMax) * 0.5, length(currentMax - currentMin) * 0.5);
    float4 previousSphere = float4((previousMin + previousMax) * 0.5, length(previousMax - previousMin) * 0.5);
    for (uint index = lane; index < work.useCount; index += 4u)
    {
        SkinBoundsUse use = SkinBoundsUses[work.firstUse + index];
        uint address = (control.output + use.output) * 48u;
        if (use.flags == 3u)
        {
            SkinCurrentBounds.Store4(address, asuint(float4(currentMin, use.error * currentStretch)));
            SkinPreviousBounds.Store4(address, asuint(float4(previousMin, use.error * previousStretch)));
        }
        else
        {
            // Cull and LOD ranges write separate words of the same output row.
            if ((use.flags & 1u) != 0u)
            {
                SkinCurrentBounds.Store3(address, asuint(currentMin));
                SkinPreviousBounds.Store3(address, asuint(previousMin));
            }
            if ((use.flags & 2u) != 0u)
            {
                SkinCurrentBounds.Store(address + 12u, asuint(use.error * currentStretch));
                SkinPreviousBounds.Store(address + 12u, asuint(use.error * previousStretch));
            }
        }
        if ((use.flags & 1u) != 0u)
        {
            SkinCurrentBounds.Store4(address + 16u, asuint(float4(currentMax, 0.0)));
            SkinPreviousBounds.Store4(address + 16u, asuint(float4(previousMax, 0.0)));
        }
        if ((use.flags & 2u) != 0u)
        {
            SkinCurrentBounds.Store4(address + 32u, asuint(currentSphere));
            SkinPreviousBounds.Store4(address + 32u, asuint(previousSphere));
        }
    }
}
