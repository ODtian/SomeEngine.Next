#ifndef PHYSICS_SDF_DATA
#define PHYSICS_SDF_DATA
struct DeviceSdfInput
{
    float3 origin; float scale;
    float3 cellSize; uint sampleStart;
    uint3 count; float traceStepScale;
    float3 min; uint fieldIndex;
    float3 max; uint padding1;
};
struct DeviceSdfSample { float distance; float3 gradient; int feature; };
RWStructuredBuffer<DeviceSdfInput> DeviceSdfInputs;
interface IDeviceSdfField
{
    DeviceSdfSample sample(DeviceSdfInput field, float3 point);
};
#endif
