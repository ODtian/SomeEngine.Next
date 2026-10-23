struct GpuPoseControl
{
    uint clipOffset;
    float sampleTime;
    uint flags;
    uint starts;
    uint2 inputNumber;
    uint2 calculationTime;
};

StructuredBuffer<GpuPoseControl> GpuPoseControls;
StructuredBuffer<uint> GpuPoseSections;
StructuredBuffer<uint4> GpuPoseWork;
ByteAddressBuffer GpuPoseClips;
RWStructuredBuffer<float4x4> GpuPoseModels;
RWStructuredBuffer<float4x4> GpuPosePrevious;
RWStructuredBuffer<float4x4> GpuPoseHistoryCurrent;
RWStructuredBuffer<float4x4> GpuPoseHistoryPrevious;

[shader("compute")]
[numthreads(64, 1, 1)]
void LoadPoseHistory(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseModels.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint section = GpuPoseSections[thread.x];
    float4x4 current = GpuPoseHistoryCurrent[thread.x];
    GpuPoseModels[thread.x] = current;
    GpuPosePrevious[thread.x] = section != 0xffffffffu && (GpuPoseControls[section].flags & 1u) != 0
        ? current : GpuPoseHistoryPrevious[thread.x];
}

struct PoseTrs { float3 translation; float4 rotation; float3 scale; };

PoseTrs DecodeTrs(uint clip, uint bone, uint sample, uint boneCount, uint samplesOffset)
{
    uint range = clip + 16u + bone * 64u;
    float3 translationMinimum = asfloat(GpuPoseClips.Load3(range));
    float3 translationExtent = asfloat(GpuPoseClips.Load3(range + 16u));
    float3 scaleMinimum = asfloat(GpuPoseClips.Load3(range + 32u));
    float3 scaleExtent = asfloat(GpuPoseClips.Load3(range + 48u));
    uint address = clip + samplesOffset + (sample * boneCount + bone) * 20u;
    uint4 words = GpuPoseClips.Load4(address);
    uint last = GpuPoseClips.Load(address + 16u);
    PoseTrs result;
    result.translation = translationMinimum + float3(words.x & 65535u, words.x >> 16u, words.y & 65535u)
        * (translationExtent / 65535.0);
    result.rotation = normalize(float4(words.y >> 16u, words.z & 65535u, words.z >> 16u, words.w & 65535u)
        * (2.0 / 65535.0) - 1.0);
    result.scale = scaleMinimum + float3(words.w >> 16u, last & 65535u, last >> 16u) * (scaleExtent / 65535.0);
    return result;
}

float4 InterpolateRotation(float4 a, float4 b, float amount)
{
    float cosine = dot(a, b);
    if (cosine < 0.0) { b = -b; cosine = -cosine; }
    if (cosine > 0.9995) return normalize(lerp(a, b, amount));
    float angle = acos(clamp(cosine, -1.0, 1.0));
    return normalize((a * sin((1.0 - amount) * angle) + b * sin(amount * angle)) / sin(angle));
}

float4x4 TrsMatrix(float3 translation, float4 q, float3 scale)
{
    float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
    float xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
    float wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;
    return float4x4(
        float4(float3(1.0 - 2.0 * (yy + zz), 2.0 * (xy + wz), 2.0 * (xz - wy)) * scale.x, 0),
        float4(float3(2.0 * (xy - wz), 1.0 - 2.0 * (xx + zz), 2.0 * (yz + wx)) * scale.y, 0),
        float4(float3(2.0 * (xz + wy), 2.0 * (yz - wx), 1.0 - 2.0 * (xx + yy)) * scale.z, 0),
        float4(translation, 1));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SamplePoseLevel(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = GpuPoseWork[thread.x];
    GpuPoseControl control = GpuPoseControls[work.x];
    if ((control.flags & 5u) != 5u) return;
    uint4 header = GpuPoseClips.Load4(control.clipOffset);
    float sample = clamp(control.sampleTime * asfloat(header.z), 0.0, float(header.y - 1u));
    uint first = uint(sample);
    PoseTrs a = DecodeTrs(control.clipOffset, work.y, first, header.x, header.w);
    PoseTrs b = DecodeTrs(control.clipOffset, work.y, min(first + 1u, header.y - 1u), header.x, header.w);
    float amount = sample - float(first);
    float4x4 local = TrsMatrix(lerp(a.translation, b.translation, amount),
        InterpolateRotation(a.rotation, b.rotation, amount), lerp(a.scale, b.scale, amount));
    GpuPoseModels[work.w] = work.z == 0xffffffffu ? local : mul(local, GpuPoseModels[work.z]);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void StorePoseHistory(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseModels.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint section = GpuPoseSections[thread.x];
    if (section == 0xffffffffu) return;
    uint flags = GpuPoseControls[section].flags;
    if ((flags & 1u) == 0) return;
    if ((flags & 2u) != 0) GpuPosePrevious[thread.x] = GpuPoseModels[thread.x];
    GpuPoseHistoryPrevious[thread.x] = GpuPosePrevious[thread.x];
    GpuPoseHistoryCurrent[thread.x] = GpuPoseModels[thread.x];
}
