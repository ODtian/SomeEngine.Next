#ifndef PHYSICS_GEOMETRY_REFERENCE_DATA
#define PHYSICS_GEOMETRY_REFERENCE_DATA
#include "physics_geometry_types.slang"
#include "physics_bodies.slang"
struct DeviceGeometryReference
{
    uint bodyCount; uint firstBody; uint secondBody; uint thirdBody;
    float3 firstLocalPoint; uint kind;
    float3 secondLocalPoint; uint gridIndex;
    float3 thirdLocalPoint; uint fieldIndex;
};
RWStructuredBuffer<DeviceGeometryReference> DeviceGeometryReferences;
RWStructuredBuffer<float3> DeviceGeometryPoints;

float3 deviceBodyPoint(uint body, float3 local)
{
    DeviceBodyState state = DeviceBodyStates[body];
    return state.position + rotateDeviceDirection(state.rotation, local);
}

#endif
