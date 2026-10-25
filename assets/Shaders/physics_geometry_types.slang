#ifndef PHYSICS_GEOMETRY_TYPES
#define PHYSICS_GEOMETRY_TYPES
struct DeviceGeometryInput
{
    float3 position;
    float convexRadius;
    float4 rotation;
    float3 halfExtents;
    float halfHeight;
    uint vertexStart;
    uint vertexCount;
    uint faceStart;
    uint faceCount;
    uint faceIndexStart;
    uint maximumFaceVertices;
    float radius;
    uint geometryIndex;
    float3 planeNormal;
    float planeOffset;
    uint geometryType;
    float3 boundsCenter;
};

float3 rotateDeviceDirection(float4 rotation, float3 direction)
{
    float3 crossProduct = 2 * cross(rotation.xyz, direction);
    return direction + rotation.w * crossProduct + cross(rotation.xyz, crossProduct);
}

#endif
