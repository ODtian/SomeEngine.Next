#ifndef SOMEENGINE_SKIN_POSE_INCLUDED
#define SOMEENGINE_SKIN_POSE_INCLUDED
#include "skin_binding.slang"

StructuredBuffer<float4x4> SkinCurrentBones;
StructuredBuffer<float4x4> SkinPreviousBones;
StructuredBuffer<uint> SkinJointIndices;
StructuredBuffer<float4x4> SkinInverseBind;

float4x4 LoadSkinModelTransform(uint2 binding, uint joint, bool previous)
{
    uint bone = SkinJointIndices[binding.x + joint];
    return previous ? SkinPreviousBones[bone] : SkinCurrentBones[bone];
}

float4x4 LoadSkinTransform(uint2 binding, uint joint, bool previous)
{
    return mul(SkinInverseBind[binding.y + joint], LoadSkinModelTransform(binding, joint, previous));
}

#endif
