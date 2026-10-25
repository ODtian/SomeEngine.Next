#ifndef PHYSICS_HULL_UPDATE
#define PHYSICS_HULL_UPDATE
#include "physics_geometry_update.slang"
#include "physics_hull_data.slang"
struct DeviceHullUpdate : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        readDeviceGeometryPose(index, geometry);
        float3 first = geometry.position + rotateDeviceDirection(geometry.rotation, HullVertices[geometry.vertexStart]);
        DeviceGeometryBounds bounds;
        bounds.min = first;
        bounds.max = first;
        for (uint vertex = 1; vertex < geometry.vertexCount; vertex++)
        {
            float3 point = geometry.position + rotateDeviceDirection(geometry.rotation, HullVertices[geometry.vertexStart + vertex]);
            bounds.min = min(bounds.min, point);
            bounds.max = max(bounds.max, point);
        }
        bounds.min -= geometry.convexRadius;
        bounds.max += geometry.convexRadius;
        return bounds;
    }
};
#endif
