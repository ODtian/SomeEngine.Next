#ifndef PHYSICS_CONTACT_TYPES
#define PHYSICS_CONTACT_TYPES
#include "physics_contact_body.slang"
struct DeviceContactConstraint
{
    uint bodyCount; uint firstBodyCount; uint bodyOffset; uint warmStart;
    float3 normal; float separation;
    float3 bodyAnchorDelta; float normalStiffness;
    float normalDamping; float staticFriction; float dynamicFriction; float restitution;
    float restitutionThreshold; uint predicted; uint2 sampleTime;
    float3 worldPoint; uint padding;
    uint2 firstGeometry; uint2 secondGeometry;
    int firstFeature; int secondFeature; int pointIndex; uint padding2;
};
#endif
