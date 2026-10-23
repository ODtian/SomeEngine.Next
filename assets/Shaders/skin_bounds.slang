struct SkinBoundsBox { float4 minimum; float4 maximum; uint joint; uint3 padding; };
struct ModelBounds { float4 minimum; float4 maximum; };

StructuredBuffer<float4x4> SkinCurrentBones;
StructuredBuffer<float4x4> SkinPreviousBones;
StructuredBuffer<uint> SkinJointIndices;
StructuredBuffer<float4x4> SkinInverseBind;
StructuredBuffer<uint4> SkinBoundsControls;
StructuredBuffer<uint4> SkinBoundsWork;
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

[shader("compute")]
[numthreads(64, 1, 1)]
void TransformSkinBounds(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    SkinBoundsWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = SkinBoundsWork[thread.x];
    uint4 control = SkinBoundsControls[work.x];
    if (control.w == 0u) return;
    float3 currentMin = 1e30.xxx, currentMax = -1e30.xxx;
    float3 previousMin = 1e30.xxx, previousMax = -1e30.xxx;
    for (uint index = 0u; index < work.z; index++)
    {
        SkinBoundsBox box = SkinBoundsBoxes[work.y + index];
        uint bone = SkinJointIndices[control.x + box.joint];
        float4x4 inverseBind = SkinInverseBind[control.y + box.joint];
        IncludeBox(box, mul(inverseBind, SkinCurrentBones[bone]), currentMin, currentMax);
        IncludeBox(box, mul(inverseBind, SkinPreviousBones[bone]), previousMin, previousMax);
    }
    ModelBounds current, previous;
    current.minimum = float4(currentMin, 0.0); current.maximum = float4(currentMax, 0.0);
    previous.minimum = float4(previousMin, 0.0); previous.maximum = float4(previousMax, 0.0);
    SkinCurrentBounds[control.z + work.w] = current;
    SkinPreviousBounds[control.z + work.w] = previous;
}
