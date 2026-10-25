#ifndef SOMEENGINE_SKIN_JOINT_TRANSFORMS_INCLUDED
#define SOMEENGINE_SKIN_JOINT_TRANSFORMS_INCLUDED

#include "skin_binding.slang"
ByteAddressBuffer SkinJointTransforms;

// Meshes participating in SkinBoundsFeature share its precomputed current/previous affine columns.
float4x4 LoadSkinJointTransform(uint2 binding, uint joint, bool previous)
{
    uint address = (binding.x + joint) * 112u + (previous ? 48u : 0u);
    float4 x = asfloat(SkinJointTransforms.Load4(address));
    float4 y = asfloat(SkinJointTransforms.Load4(address + 16u));
    float4 z = asfloat(SkinJointTransforms.Load4(address + 32u));
    return float4x4(float4(x.x, y.x, z.x, 0), float4(x.y, y.y, z.y, 0),
        float4(x.z, y.z, z.z, 0), float4(x.w, y.w, z.w, 1));
}

#endif
