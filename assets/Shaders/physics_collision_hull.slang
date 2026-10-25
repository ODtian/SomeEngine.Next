#ifndef PHYSICS_COLLISION_HULL
#define PHYSICS_COLLISION_HULL
#include "physics_collision.slang"

struct DeviceHullFace { float3 normal; uint firstIndex; uint indexCount; };
RWStructuredBuffer<float3> HullVertices;
RWStructuredBuffer<DeviceHullFace> HullFaces;
RWStructuredBuffer<uint> HullFaceIndices;

struct DeviceHull : IDeviceConvex
{
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), direction);
        uint selected = 0;
        for (uint index = 1; index < geometry.vertexCount; index++)
            if (dot(HullVertices[geometry.vertexStart + index], local) > dot(HullVertices[geometry.vertexStart + selected], local))
                selected = index;
        DeviceFeaturePoint point;
        point.point = geometry.position + rotateDeviceDirection(geometry.rotation, HullVertices[geometry.vertexStart + selected]);
        point.feature = int(geometry.faceCount + selected);
        return point;
    }
    float radius(DeviceGeometryInput geometry) { return geometry.convexRadius; }
    uint face(DeviceGeometryInput geometry, float3 direction, uint output)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), direction);
        uint selected = 0;
        for (uint index = 1; index < geometry.faceCount; index++)
            if (dot(HullFaces[geometry.faceStart + index].normal, local) > dot(HullFaces[geometry.faceStart + selected].normal, local))
                selected = index;
        DeviceHullFace face = HullFaces[geometry.faceStart + selected];
        for (uint index = 0; index < face.indexCount; index++)
        {
            uint vertex = HullFaceIndices[geometry.faceIndexStart + face.firstIndex + index];
            DeviceFeaturePoint point;
            point.point = geometry.position + rotateDeviceDirection(geometry.rotation,
                HullVertices[geometry.vertexStart + vertex] + face.normal * geometry.convexRadius);
            point.feature = int(selected);
            CollisionFaces[output + index] = point;
        }
        return face.indexCount;
    }
};

#endif
