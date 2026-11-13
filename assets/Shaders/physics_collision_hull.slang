#ifndef PHYSICS_COLLISION_HULL
#define PHYSICS_COLLISION_HULL
#include "physics_collision.slang"

#include "physics_hull_data.slang"

struct DeviceHull : IDevicePointGeometry
{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), point - geometry.position);
        float planeDistance = -3.402823466e38f;
        uint nearestFace = 0;
        for (uint index = 0; index < geometry.faceCount; index++)
        {
            DeviceHullFace face = HullFaces[geometry.faceStart + index];
            uint vertex = HullFaceIndices[geometry.faceIndexStart + face.firstIndex];
            float distance = dot(face.normal, local - HullVertices[geometry.vertexStart + vertex]);
            if (distance > planeDistance)
            {
                planeDistance = distance;
                nearestFace = index;
            }
        }
        DeviceHullFace selected = HullFaces[geometry.faceStart + nearestFace];
        float3 closest = local - selected.normal * planeDistance;
        bool inside = true;
        for (uint edge = 0; edge < selected.indexCount; edge++)
        {
            float3 first = HullVertices[geometry.vertexStart + HullFaceIndices[geometry.faceIndexStart + selected.firstIndex + edge]];
            float3 second = HullVertices[geometry.vertexStart + HullFaceIndices[geometry.faceIndexStart + selected.firstIndex + (edge + 1) % selected.indexCount]];
            if (dot(cross(second - first, closest - first), selected.normal) < 0) inside = false;
        }
        float3 normal = selected.normal;
        float separation = planeDistance;
        if (planeDistance > 0 && !inside)
        {
            float squared = 3.402823466e38f;
            for (uint index = 0; index < geometry.faceCount; index++)
            {
                DeviceHullFace face = HullFaces[geometry.faceStart + index];
                float3 first = HullVertices[geometry.vertexStart + HullFaceIndices[geometry.faceIndexStart + face.firstIndex]];
                for (uint triangle = 1; triangle + 1 < face.indexCount; triangle++)
                {
                    float3 second = HullVertices[geometry.vertexStart + HullFaceIndices[geometry.faceIndexStart + face.firstIndex + triangle]];
                    float3 third = HullVertices[geometry.vertexStart + HullFaceIndices[geometry.faceIndexStart + face.firstIndex + triangle + 1]];
                    float3 weights;
                    float3 offset = closestDeviceTriangle(first - local, second - local, third - local, weights);
                    float distance = dot(offset, offset);
                    if (distance < squared)
                    {
                        squared = distance;
                        closest = local + offset;
                        nearestFace = index;
                    }
                }
            }
            separation = sqrt(squared);
            normal = (local - closest) / separation;
        }
        return deviceLocalPointDistance(geometry, closest + normal * geometry.convexRadius,
            normal, separation - geometry.convexRadius, int(nearestFace));
    }
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
