#ifndef PHYSICS_GEOMETRY_REFERENCE_DATA
#define PHYSICS_GEOMETRY_REFERENCE_DATA
#include "physics_geometry_types.slang"
#include "physics_bodies.slang"
#include "physics_constraint_math.slang"
struct DeviceGeometryReference
{
    uint bodyCount; uint firstBody; uint secondBody; uint thirdBody;
    float3 firstLocalPoint; uint kind;
    float3 secondLocalPoint; uint gridIndex;
    float3 thirdLocalPoint; uint fieldIndex;
};
RWStructuredBuffer<DeviceGeometryReference, ScalarDataLayout> DeviceGeometryReferences;
RWStructuredBuffer<float3, ScalarDataLayout> DeviceGeometryPoints;

float3 deviceBodyPoint(uint body, float3 local)
{
    DeviceBodyState state = DeviceBodyStates[body];
    return state.position + rotateDeviceDirection(state.rotation, local);
}

DeviceBodyState deviceGeometryBody(uint index, uint sample)
{
    DeviceBodyState body = DeviceBodyStates[index];
    if (sample == 1) return deviceBodyStart(body);
    if (sample == 2)
    {
        body.rotation = body.predictedRotation;
        bodySetCenterOfMass(body, body.predictedCenterOfMass);
    }
    return body;
}
float3 deviceGeometryPoint(uint body, float3 local, uint sample)
{
    DeviceBodyState state = deviceGeometryBody(body, sample);
    return state.position + rotateDeviceDirection(state.rotation, local);
}
interface IDeviceGeometryPointSample { float3 read(DeviceGeometryInput geometry, uint point); };
struct DeviceCurrentGeometryPoints : IDeviceGeometryPointSample
{
    float3 read(DeviceGeometryInput geometry, uint point) { return DeviceGeometryPoints[geometry.geometryIndex * 3 + point]; }
};
struct DeviceStartGeometryPoints : IDeviceGeometryPointSample
{
    float3 read(DeviceGeometryInput geometry, uint point)
    {
        DeviceGeometryReference reference = DeviceGeometryReferences[geometry.geometryIndex];
        uint bodies[3] = { reference.firstBody, reference.secondBody, reference.thirdBody };
        float3 local[3] = { reference.firstLocalPoint, reference.secondLocalPoint, reference.thirdLocalPoint };
        return deviceGeometryPoint(bodies[point], local[point], geometry.sample);
    }
};
DeviceGeometryInput deviceGeometryStart(DeviceGeometryInput geometry)
{
    geometry.sample = 1;
    DeviceGeometryReference reference = DeviceGeometryReferences[geometry.geometryIndex];
    DeviceBodyState first = deviceGeometryBody(reference.firstBody, 1);
    geometry.position = first.position; geometry.rotation = first.rotation;
    if (reference.kind == 1)
    {
        DeviceStartGeometryPoints points;
        float3 a = points.read(geometry, 0), b = points.read(geometry, 1);
        float3 edge = b - a;
        float size = length(edge);
        float3 y = edge / size;
        geometry.rotation = y.y < -.99999f ? float4(1,0,0,0) : normalize(float4(cross(float3(0,1,0),y),1+y.y));
        geometry.position = (a+b)*.5f; geometry.halfHeight = size*.5f;
    }
    return geometry;
}

#endif
