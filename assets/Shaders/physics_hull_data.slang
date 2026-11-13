#ifndef PHYSICS_HULL_DATA
#define PHYSICS_HULL_DATA
struct DeviceHullFace { float3 normal; uint firstIndex; uint indexCount; };
RWStructuredBuffer<float3, ScalarDataLayout> HullVertices;
RWStructuredBuffer<DeviceHullFace, ScalarDataLayout> HullFaces;
RWStructuredBuffer<uint, ScalarDataLayout> HullFaceIndices;
#endif
