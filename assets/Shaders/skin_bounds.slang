struct SkinBoundsBox { float4 minimum; float4 maximum; uint joint; uint padding0; uint padding1; uint padding2; };
struct ModelBounds { float4 minimum; float4 maximum; float4 lodSphere; };
struct SkinBoundsGroup
{
    uint instance;
    uint firstBox;
    uint boxCount;
    uint output;
    uint firstLodBox;
    uint lodBoxCount;
    float minError;
    float maxParentError;
};

StructuredBuffer<float4x4> SkinCurrentBones;
StructuredBuffer<float4x4> SkinPreviousBones;
StructuredBuffer<uint> SkinJointIndices;
StructuredBuffer<float4x4> SkinInverseBind;
StructuredBuffer<uint4> SkinBoundsControls;
StructuredBuffer<SkinBoundsGroup> SkinBoundsWork;
StructuredBuffer<SkinBoundsBox> SkinBoundsBoxes;
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

void ReadSkinTransforms(SkinBoundsBox box, uint4 control, out float4x4 current, out float4x4 previous)
{
    uint bone = SkinJointIndices[control.x + box.joint];
    float4x4 inverseBind = SkinInverseBind[control.y + box.joint];
    current = mul(inverseBind, SkinCurrentBones[bone]);
    previous = mul(inverseBind, SkinPreviousBones[bone]);
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
void TransformSkinBounds(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    SkinBoundsWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    SkinBoundsGroup work = SkinBoundsWork[thread.x];
    uint4 control = SkinBoundsControls[work.instance];
    if (control.w == 0u) return;
    float3 currentMin = 1e30.xxx, currentMax = -1e30.xxx;
    float3 previousMin = 1e30.xxx, previousMax = -1e30.xxx;
    for (uint index = 0u; index < work.boxCount; index++)
    {
        SkinBoundsBox box = SkinBoundsBoxes[work.firstBox + index];
        float4x4 current, previous;
        ReadSkinTransforms(box, control, current, previous);
        IncludeBox(box, current, currentMin, currentMax);
        IncludeBox(box, previous, previousMin, previousMax);
    }
    float3 currentLodMin = 1e30.xxx, currentLodMax = -1e30.xxx;
    float3 previousLodMin = 1e30.xxx, previousLodMax = -1e30.xxx;
    float currentStretch = 0.0, previousStretch = 0.0;
    for (uint index = 0u; index < work.lodBoxCount; index++)
    {
        SkinBoundsBox box = SkinBoundsBoxes[work.firstLodBox + index];
        float4x4 current, previous;
        ReadSkinTransforms(box, control, current, previous);
        IncludeBox(box, current, currentLodMin, currentLodMax);
        IncludeBox(box, previous, previousLodMin, previousLodMax);
        currentStretch = max(currentStretch, SkinTransformStretch(current));
        previousStretch = max(previousStretch, SkinTransformStretch(previous));
    }
    ModelBounds current, previous;
    current.minimum = float4(currentMin, work.minError * currentStretch);
    current.maximum = float4(currentMax, work.maxParentError * currentStretch);
    current.lodSphere = float4((currentLodMin + currentLodMax) * 0.5, length(currentLodMax - currentLodMin) * 0.5);
    previous.minimum = float4(previousMin, work.minError * previousStretch);
    previous.maximum = float4(previousMax, work.maxParentError * previousStretch);
    previous.lodSphere = float4((previousLodMin + previousLodMax) * 0.5, length(previousLodMax - previousLodMin) * 0.5);
    SkinCurrentBounds[control.z + work.output] = current;
    SkinPreviousBounds[control.z + work.output] = previous;
}
