#ifndef PHYSICS_HULL_DATA
#define PHYSICS_HULL_DATA
struct DeviceHullFace { float3 normal; uint firstIndex; uint indexCount; };
RWStructuredBuffer<float3> HullVertices;
RWStructuredBuffer<DeviceHullFace> HullFaces;
RWStructuredBuffer<uint> HullFaceIndices;
#endif
