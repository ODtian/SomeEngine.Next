#ifndef PHYSICS_GEOMETRY_REFERENCES
#define PHYSICS_GEOMETRY_REFERENCES
#include "physics_collision.slang"
#include "physics_bodies.slang"

#include "physics_geometry_reference_data.slang"
#include "physics_contact_body.slang"
RWStructuredBuffer<DeviceContactBody> CollisionContactBodies;
RWStructuredBuffer<uint> CollisionContactBodyCounts;

struct DeviceTriangle : IDevicePointGeometry
{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point)
    {
        uint start = geometry.geometryIndex*3;
        float3 a=DeviceGeometryPoints[start], b=DeviceGeometryPoints[start+1], c=DeviceGeometryPoints[start+2];
        float3 normal = normalize(cross(b-a,c-a));
        float height = dot(normal,point-a);
        float3 weights;
        float3 delta = closestDeviceTriangle(a-point,b-point,c-point,weights);
        float size = length(delta);
        float3 direction = size > 0 ? -delta/size : (height>=0?normal:-normal);
        int feature = weights.x>=1?2:weights.y>=1?3:weights.z>=1?4:weights.x<=0?5:weights.y<=0?6:weights.z<=0?7:height>=0?0:1;
        DeviceGeometryPointDistance result;
        result.point = point+delta+direction*geometry.convexRadius;
        result.normal = direction; result.distance = size-geometry.convexRadius; result.feature = feature;
        return result;
    }
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction)
    {
        uint first = geometry.geometryIndex * 3;
        DeviceFeaturePoint result;
        result.point = DeviceGeometryPoints[first];
        result.feature = 2;
        for (uint index = 1; index < 3; index++)
        {
            float3 point = DeviceGeometryPoints[first + index];
            if (dot(point, direction) > dot(result.point, direction))
            { result.point = point; result.feature = int(index + 2); }
        }
        return result;
    }
    float radius(DeviceGeometryInput geometry) { return geometry.convexRadius; }
    uint face(DeviceGeometryInput geometry, float3 direction, uint output)
    {
        uint first = geometry.geometryIndex * 3;
        float3 a = DeviceGeometryPoints[first], b = DeviceGeometryPoints[first + 1], c = DeviceGeometryPoints[first + 2];
        float3 normal = normalize(cross(b - a, c - a));
        bool front = dot(normal, direction) >= 0;
        float3 offset = normal * (front ? geometry.convexRadius : -geometry.convexRadius);
        DeviceFeaturePoint point;
        point.feature = front ? 0 : 1;
        point.point = a + offset; CollisionFaces[output] = point;
        point.point = (front ? b : c) + offset; CollisionFaces[output + 1] = point;
        point.point = (front ? c : b) + offset; CollisionFaces[output + 2] = point;
        return 3;
    }
};

uint writeDeviceGeometryBodies(uint geometryIndex, float3 point, uint output)
{
    DeviceGeometryReference reference = DeviceGeometryReferences[geometryIndex];
    uint indices[3] = { reference.firstBody, reference.secondBody, reference.thirdBody };
    float3 local[3] = { reference.firstLocalPoint, reference.secondLocalPoint, reference.thirdLocalPoint };
    float3 weights = float3(1, 0, 0);
    if (reference.kind == 1)
    {
        float3 first = deviceBodyPoint(indices[0], local[0]);
        float3 second = deviceBodyPoint(indices[1], local[1]);
        float3 edge = second - first;
        float parameter = saturate(dot(point - first, edge) / dot(edge, edge));
        weights = float3(1 - parameter, parameter, 0);
    }
    else if (reference.kind == 2)
    {
        float3 first = deviceBodyPoint(indices[0], local[0]);
        float3 second = deviceBodyPoint(indices[1], local[1]);
        float3 third = deviceBodyPoint(indices[2], local[2]);
        closestDeviceTriangle(first - point, second - point, third - point, weights);
    }
    [loop] for (uint index = 0; index < reference.bodyCount; index++)
    {
        DeviceBodyState state = DeviceBodyStates[indices[index]];
        DeviceContactBody body;
        body.entity = uint2(state.entityIndex, state.entityGeneration);
        body.localPoint = reference.kind == 0
            ? rotateDeviceDirection(float4(-state.rotation.xyz, state.rotation.w), point - state.position) : local[index];
        body.weight = weights[index];
        body.stateIndex = indices[index];
        body.padding = 0;
        CollisionContactBodies[output + index] = body;
    }
    return reference.bodyCount;
}

void detectDeviceReferencedCollision<TKernel : IDeviceCollisionNarrowPhase>(uint work, TKernel kernel)
{
    detectDeviceCollision(work, kernel);
    if (work >= DetectionParameters.workItems) return;
    uint start = DetectionBinOffsets[DetectionParameters.bin];
    uint end = DetectionBinOffsets[DetectionParameters.bin + 1];
    [loop] for (uint index = work; index < end - start; index += DetectionParameters.workItems)
    {
        DeviceCollisionPair pair = DetectionPairs[start + index];
        DeviceGeometryInput first = DeviceGeometries[pair.firstGeometry];
        DeviceGeometryInput second = DeviceGeometries[pair.secondGeometry];
        uint count = CollisionContactCounts[index];
        [loop] for (uint point = 0; point < count; point++)
        {
            uint contactIndex = index * 4 + point;
            DeviceCollisionContact contact = CollisionContacts[contactIndex];
            float3 firstPoint = first.position + rotateDeviceDirection(first.rotation, contact.firstLocalPoint);
            float3 secondPoint = second.position + rotateDeviceDirection(second.rotation, contact.secondLocalPoint);
            uint firstCount = writeDeviceGeometryBodies(pair.firstGeometry, firstPoint, contactIndex * 6);
            uint secondCount = writeDeviceGeometryBodies(pair.secondGeometry, secondPoint, contactIndex * 6 + firstCount);
            contact.firstBodyCount = int(firstCount);
            CollisionContacts[contactIndex] = contact;
            CollisionContactBodyCounts[contactIndex] = firstCount + secondCount;
        }
    }
}
#endif
