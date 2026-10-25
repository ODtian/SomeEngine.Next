#ifndef PHYSICS_GEOMETRY_UPDATE
#define PHYSICS_GEOMETRY_UPDATE
#include "physics_geometry_reference_data.slang"
RWStructuredBuffer<DeviceGeometryInput> DeviceGeometries;

struct GeometryCollisionInput
{
    float3 min; uint entityIndex;
    float3 max; uint entityGeneration;
    uint geometryType; uint geometryIndex; uint members; uint revision;
    uint2 key; uint2 categories; uint2 allowed; uint hasKey; uint userValue;
};
struct DeviceGeometryBounds { float3 min; float3 max; };
struct DeviceGeometryUpdateParameters { uint count; uint geometryType; };
ConstantBuffer<DeviceGeometryUpdateParameters> GeometryUpdateParameters;
RWStructuredBuffer<GeometryCollisionInput> GeometryCollisionInputs;

interface IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry);
};

void readDeviceGeometryPose(uint index, inout DeviceGeometryInput geometry)
{
    DeviceBodyState body = DeviceBodyStates[DeviceGeometryReferences[index].firstBody];
    geometry.position = body.position;
    geometry.rotation = body.rotation;
}

struct DeviceBoxUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        readDeviceGeometryPose(index, geometry);
        float3 half = geometry.halfExtents;
        float3 radius = abs(rotateDeviceDirection(geometry.rotation, float3(half.x, 0, 0)))
            + abs(rotateDeviceDirection(geometry.rotation, float3(0, half.y, 0)))
            + abs(rotateDeviceDirection(geometry.rotation, float3(0, 0, half.z)));
        DeviceGeometryBounds bounds;
        bounds.min = geometry.position - radius;
        bounds.max = geometry.position + radius;
        return bounds;
    }
};

struct DeviceCapsuleUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        readDeviceGeometryPose(index, geometry);
        float3 radius = abs(rotateDeviceDirection(geometry.rotation, float3(0, geometry.halfHeight, 0))) + geometry.convexRadius;
        DeviceGeometryBounds bounds;
        bounds.min = geometry.position - radius;
        bounds.max = geometry.position + radius;
        return bounds;
    }
};

struct DeviceCylinderUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        readDeviceGeometryPose(index, geometry);
        float3 axis = rotateDeviceDirection(geometry.rotation, float3(0, 1, 0));
        float3 radius = sqrt(max(float3(0), 1 - axis * axis)) * geometry.radius + abs(axis) * geometry.halfHeight;
        DeviceGeometryBounds bounds;
        bounds.min = geometry.position - radius;
        bounds.max = geometry.position + radius;
        return bounds;
    }
};

struct DeviceSphereUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        DeviceBodyState body = DeviceBodyStates[DeviceGeometryReferences[index].firstBody];
        geometry.position = body.position;
        geometry.rotation = body.rotation;
        DeviceGeometryBounds bounds;
        bounds.min = geometry.position - geometry.convexRadius;
        bounds.max = geometry.position + geometry.convexRadius;
        return bounds;
    }
};

struct DeviceDynamicCapsuleUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        DeviceGeometryReference reference = DeviceGeometryReferences[index];
        float3 first = deviceBodyPoint(reference.firstBody, reference.firstLocalPoint);
        float3 second = deviceBodyPoint(reference.secondBody, reference.secondLocalPoint);
        DeviceGeometryPoints[index * 3] = first;
        DeviceGeometryPoints[index * 3 + 1] = second;
        float3 edge = second - first;
        float size = length(edge);
        float3 y = edge / size;
        geometry.rotation = y.y < -0.99999f ? float4(1, 0, 0, 0)
            : normalize(float4(cross(float3(0, 1, 0), y), 1 + y.y));
        geometry.position = (first + second) * .5f;
        geometry.halfHeight = size * .5f;
        DeviceGeometryBounds bounds;
        bounds.min = min(first, second) - geometry.convexRadius;
        bounds.max = max(first, second) + geometry.convexRadius;
        return bounds;
    }
};

struct DevicePlaneUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        DeviceBodyState body = DeviceBodyStates[DeviceGeometryReferences[index].firstBody];
        geometry.position = body.position;
        geometry.rotation = body.rotation;
        DeviceGeometryBounds bounds;
        bounds.min = geometry.boundsCenter - geometry.halfExtents;
        bounds.max = geometry.boundsCenter + geometry.halfExtents;
        return bounds;
    }
};

struct DeviceDynamicFaceUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        DeviceGeometryReference reference = DeviceGeometryReferences[index];
        float3 first = deviceBodyPoint(reference.firstBody, reference.firstLocalPoint);
        float3 second = deviceBodyPoint(reference.secondBody, reference.secondLocalPoint);
        float3 third = deviceBodyPoint(reference.thirdBody, reference.thirdLocalPoint);
        DeviceGeometryPoints[index * 3] = first;
        DeviceGeometryPoints[index * 3 + 1] = second;
        DeviceGeometryPoints[index * 3 + 2] = third;
        DeviceBodyState body = DeviceBodyStates[reference.firstBody];
        geometry.position = body.position;
        geometry.rotation = body.rotation;
        DeviceGeometryBounds bounds;
        bounds.min = min(first, min(second, third)) - geometry.convexRadius;
        bounds.max = max(first, max(second, third)) + geometry.convexRadius;
        return bounds;
    }
};

void updateDeviceGeometry<TUpdate : IDeviceGeometryUpdate>(uint index)
{
    if (index >= GeometryUpdateParameters.count) return;
    DeviceGeometryInput geometry = DeviceGeometries[index];
    if (geometry.geometryType != GeometryUpdateParameters.geometryType) return;
    DeviceGeometryReference reference = DeviceGeometryReferences[index];
    if (reference.bodyCount == 0) return;
    TUpdate update;
    DeviceGeometryBounds bounds = update.update(index, geometry);
    DeviceGeometries[index] = geometry;
    GeometryCollisionInput input = GeometryCollisionInputs[reference.gridIndex];
    input.min = bounds.min;
    input.max = bounds.max;
    GeometryCollisionInputs[reference.gridIndex] = input;
}
#endif
