#ifndef PHYSICS_SDF_GRID
#define PHYSICS_SDF_GRID
#include "physics_sdf_data.slang"
RWStructuredBuffer<float> DeviceSdfSamples;
struct DeviceTrilinearSdfGrid : IDeviceSdfField
{
    DeviceSdfSample sample(DeviceSdfInput field, float3 point)
    {
        float3 coordinate = (point - field.origin) / field.cellSize;
        int3 cell = clamp(int3(floor(coordinate)), int3(0), int3(field.count) - 2);
        float3 f = coordinate - float3(cell);
        uint start = field.sampleStart + uint(cell.x) + field.count.x * (uint(cell.y) + field.count.y * uint(cell.z));
        uint plane = field.count.x * field.count.y;
        float lowerLeft = DeviceSdfSamples[start], lowerRight = DeviceSdfSamples[start + 1];
        float upperLeft = DeviceSdfSamples[start + field.count.x], upperRight = DeviceSdfSamples[start + field.count.x + 1];
        float backLowerLeft = DeviceSdfSamples[start + plane], backLowerRight = DeviceSdfSamples[start + plane + 1];
        float backUpperLeft = DeviceSdfSamples[start + plane + field.count.x], backUpperRight = DeviceSdfSamples[start + plane + field.count.x + 1];
        float lower = lerp(lowerLeft, lowerRight, f.x), upper = lerp(upperLeft, upperRight, f.x);
        float backLower = lerp(backLowerLeft, backLowerRight, f.x), backUpper = lerp(backUpperLeft, backUpperRight, f.x);
        float front = lerp(lower, upper, f.y), back = lerp(backLower, backUpper, f.y);
        DeviceSdfSample result;
        result.distance = lerp(front, back, f.z);
        result.gradient = float3(
            lerp(lerp(lowerRight - lowerLeft, upperRight - upperLeft, f.y),
                lerp(backLowerRight - backLowerLeft, backUpperRight - backUpperLeft, f.y), f.z) / field.cellSize.x,
            lerp(upper - lower, backUpper - backLower, f.z) / field.cellSize.y,
            (back - front) / field.cellSize.z);
        result.feature = cell.x + int(field.count.x - 1) * (cell.y + int(field.count.y - 1) * cell.z);
        return result;
    }
};
#endif
