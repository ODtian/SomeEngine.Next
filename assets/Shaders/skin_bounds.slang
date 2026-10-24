struct SkinBoundsBox { float4 minimum; float4 maximum; uint joint; uint3 padding; };
struct ModelBounds { float4 minimum; float4 maximum; float4 lodSphere; };
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
    float4x4 current;
    float4x4 previous;
    float2 stretch;
    float2 padding;
};
struct SkinBoundsGroup
{
    uint instance;
    uint firstBox;
    uint boxCount;
    uint output;
    uint firstLodBox;
    uint lodBoxCount;
    float error;
    uint padding;
};

StructuredBuffer<float4x4> SkinCurrentBones;
StructuredBuffer<float4x4> SkinPreviousBones;
StructuredBuffer<uint> SkinJointIndices;
StructuredBuffer<float4x4> SkinInverseBind;
StructuredBuffer<SkinBoundsControl> SkinBoundsControls;
StructuredBuffer<SkinBoundsGroup> SkinBoundsWork;
StructuredBuffer<SkinBoundsBox> SkinBoundsBoxes;
RWStructuredBuffer<SkinBoundsTransform> SkinJointTransforms;
RWStructuredBuffer<ModelBounds> SkinCurrentBounds;
RWStructuredBuffer<ModelBounds> SkinPreviousBounds;

void IncludeBox(SkinBoundsBox box, float4x4 transform, inout float3 minimum, inout float3 maximum)
{
    float3 center = (box.minimum.xyz + box.maximum.xyz) * 0.5;
    float3 extent = (box.maximum.xyz - box.minimum.xyz) * 0.5;
    float3 transformed = mul(float4(center, 1.0), transform).xyz;
    float3 radius = abs(transform[0].xyz) * extent.x + abs(transform[1].xyz) * extent.y + abs(transform[2].xyz) * extent.z;
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
    SkinBoundsTransform result;
    result.current = mul(inverseBind, SkinCurrentBones[bone]);
    result.previous = mul(inverseBind, SkinPreviousBones[bone]);
    result.stretch = float2(SkinTransformStretch(result.current), SkinTransformStretch(result.previous));
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
    bool sameBoxes = work.firstBox == work.firstLodBox && work.boxCount == work.lodBoxCount;
    float currentStretch = 0.0, previousStretch = 0.0;
    for (uint index = lane; index < work.boxCount; index += 4u)
    {
        SkinBoundsBox box = SkinBoundsBoxes[work.firstBox + index];
        SkinBoundsTransform transform = SkinJointTransforms[control.joints + box.joint];
        IncludeBox(box, transform.current, currentMin, currentMax);
        IncludeBox(box, transform.previous, previousMin, previousMax);
        if (sameBoxes)
        {
            currentStretch = max(currentStretch, transform.stretch.x);
            previousStretch = max(previousStretch, transform.stretch.y);
        }
    }
    float3 currentLodMin = 1e30.xxx, currentLodMax = -1e30.xxx;
    float3 previousLodMin = 1e30.xxx, previousLodMax = -1e30.xxx;
    if (sameBoxes)
    {
        currentLodMin = currentMin; currentLodMax = currentMax;
        previousLodMin = previousMin; previousLodMax = previousMax;
    }
    else
    {
        for (uint index = lane; index < work.lodBoxCount; index += 4u)
        {
            SkinBoundsBox box = SkinBoundsBoxes[work.firstLodBox + index];
            SkinBoundsTransform transform = SkinJointTransforms[control.joints + box.joint];
            IncludeBox(box, transform.current, currentLodMin, currentLodMax);
            IncludeBox(box, transform.previous, previousLodMin, previousLodMax);
            currentStretch = max(currentStretch, transform.stretch.x);
            previousStretch = max(previousStretch, transform.stretch.y);
        }
    }
    [unroll]
    for (uint width = 1u; width < 4u; width *= 2u)
    {
        uint other = WaveGetLaneIndex() ^ width;
        currentMin = min(currentMin, WaveReadLaneAt(currentMin, other));
        currentMax = max(currentMax, WaveReadLaneAt(currentMax, other));
        previousMin = min(previousMin, WaveReadLaneAt(previousMin, other));
        previousMax = max(previousMax, WaveReadLaneAt(previousMax, other));
        currentLodMin = min(currentLodMin, WaveReadLaneAt(currentLodMin, other));
        currentLodMax = max(currentLodMax, WaveReadLaneAt(currentLodMax, other));
        previousLodMin = min(previousLodMin, WaveReadLaneAt(previousLodMin, other));
        previousLodMax = max(previousLodMax, WaveReadLaneAt(previousLodMax, other));
        currentStretch = max(currentStretch, WaveReadLaneAt(currentStretch, other));
        previousStretch = max(previousStretch, WaveReadLaneAt(previousStretch, other));
    }
    if (lane != 0u) return;
    ModelBounds current, previous;
    current.minimum = float4(currentMin, work.error * currentStretch);
    current.maximum = float4(currentMax, 0.0);
    current.lodSphere = float4((currentLodMin + currentLodMax) * 0.5, length(currentLodMax - currentLodMin) * 0.5);
    previous.minimum = float4(previousMin, work.error * previousStretch);
    previous.maximum = float4(previousMax, 0.0);
    previous.lodSphere = float4((previousLodMin + previousLodMax) * 0.5, length(previousLodMax - previousLodMin) * 0.5);
    SkinCurrentBounds[control.output + work.output] = current;
    SkinPreviousBounds[control.output + work.output] = previous;
}
