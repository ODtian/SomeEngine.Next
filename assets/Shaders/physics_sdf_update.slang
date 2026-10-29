#ifndef PHYSICS_SDF_UPDATE
#define PHYSICS_SDF_UPDATE
#include "physics_geometry_update.slang"
#include "physics_sdf_data.slang"
struct DeviceSdfUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        DeviceSdfInput field = DeviceSdfInputs[DeviceGeometryReferences[index].fieldIndex];
        DeviceBodyState body = DeviceBodyStates[DeviceGeometryReferences[index].firstBody];
        geometry.position = body.position;
        geometry.rotation = body.rotation;
        float3 center = geometry.position + rotateDeviceDirection(geometry.rotation, (field.min + field.max) * (.5f * field.scale));
        float3 half = (field.max - field.min) * (.5f * field.scale);
        float3 radius = abs(rotateDeviceDirection(geometry.rotation, float3(half.x,0,0)))
            + abs(rotateDeviceDirection(geometry.rotation, float3(0,half.y,0)))
            + abs(rotateDeviceDirection(geometry.rotation, float3(0,0,half.z)));
        DeviceGeometryBounds result;
        result.min = center - radius; result.max = center + radius;
        return result;
    }
};
#endif
