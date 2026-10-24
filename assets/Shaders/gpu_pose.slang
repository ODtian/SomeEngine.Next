struct GpuPoseControl
{
    uint clipOffset;
    float sampleTime;
    uint flags;
    uint starts;
    uint2 inputNumber;
    uint2 calculationTime;
    uint machineOffset;
    uint inputOffset;
    float playbackTime;
    uint padding;
};

struct GpuPosePlayback
{
    uint state;
    uint sourceState;
    uint starts;
    uint transitionIndex;
    uint2 inputNumber;
    uint2 calculationTime;
    float time;
    float sourceTime;
    float playbackTime;
    float transitionTime;
    float sampleTime;
    float sourceSampleTime;
    float weight;
    uint finished;
};

StructuredBuffer<GpuPoseControl> GpuPoseControls;
StructuredBuffer<float> GpuPoseInputs;
RWStructuredBuffer<GpuPosePlayback> GpuPosePlaybackStates;
StructuredBuffer<uint> GpuPoseSections;
StructuredBuffer<uint4> GpuPoseWork;
ByteAddressBuffer GpuPoseClips;
RWStructuredBuffer<float4x4> GpuPoseModels;
RWStructuredBuffer<float4x4> GpuPosePrevious;
RWStructuredBuffer<float4x4> GpuPoseHistoryCurrent;
RWStructuredBuffer<float4x4> GpuPoseHistoryPrevious;

struct PoseStateDefinition
{
    uint clipOffset;
    float duration;
    float speed;
    uint loop;
    uint firstTransition;
    uint transitionCount;
};

PoseStateDefinition ReadPoseState(uint4 machine, uint state)
{
    uint address = machine.x + state * 24u;
    uint4 values = GpuPoseClips.Load4(address);
    uint2 transitions = GpuPoseClips.Load2(address + 16u);
    PoseStateDefinition result;
    result.clipOffset = values.x;
    result.duration = asfloat(values.y);
    result.speed = asfloat(values.z);
    result.loop = values.w;
    result.firstTransition = transitions.x;
    result.transitionCount = transitions.y;
    return result;
}

bool ComparePoseInput(float value, uint comparison, float threshold)
{
    switch (comparison)
    {
        case 0u: return value == threshold;
        case 1u: return value != threshold;
        case 2u: return value < threshold;
        case 3u: return value <= threshold;
        case 4u: return value > threshold;
        default: return value >= threshold;
    }
}

float PoseSampleTime(PoseStateDefinition state, float time)
{
    return state.loop != 0u ? fmod(time, state.duration) : min(time, state.duration);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void AdvancePoseStates(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseControls.GetDimensions(count, stride);
    if (thread.x >= count) return;
    GpuPoseControl control = GpuPoseControls[thread.x];
    if ((control.flags & 1u) == 0u) return;
    GpuPosePlayback playback = GpuPosePlaybackStates[thread.x];
    bool reset = (control.flags & 34u) != 0u || playback.starts != control.starts
        || control.playbackTime < playback.playbackTime;
    if (!reset && all(playback.inputNumber == control.inputNumber)) return;
    if ((control.flags & 8u) == 0u)
    {
        playback = (GpuPosePlayback)0;
        playback.state = playback.sourceState = 0xffffffffu;
        playback.transitionIndex = 0xffffffffu;
        playback.time = playback.sourceTime = control.sampleTime;
        playback.sampleTime = playback.sourceSampleTime = control.sampleTime;
        playback.weight = 1.0;
    }
    else
    {
        uint4 machine = GpuPoseClips.Load4(control.machineOffset);
        if (reset)
        {
            playback = (GpuPosePlayback)0;
            playback.state = playback.sourceState = machine.z;
            playback.transitionIndex = 0xffffffffu;
            playback.weight = 1.0;
        }
        if ((control.flags & 16u) != 0u)
        {
            if (playback.transitionIndex == 0xffffffffu)
            {
                PoseStateDefinition source = ReadPoseState(machine, playback.state);
                for (uint offset = 0u; offset < source.transitionCount; offset++)
                {
                    uint index = source.firstTransition + offset;
                    uint address = machine.y + index * 20u;
                    uint4 candidate = GpuPoseClips.Load4(address);
                    float value = GpuPoseInputs[control.inputOffset + candidate.y];
                    if (!ComparePoseInput(value, candidate.z, asfloat(candidate.w))) continue;
                    playback.sourceState = playback.state;
                    playback.sourceTime = playback.time;
                    playback.state = candidate.x;
                    playback.time = 0.0;
                    playback.transitionTime = 0.0;
                    float duration = asfloat(GpuPoseClips.Load(address + 16u));
                    playback.transitionIndex = duration == 0.0 ? 0xffffffffu : index;
                    break;
                }
            }
            float delta = control.playbackTime - playback.playbackTime;
            PoseStateDefinition target = ReadPoseState(machine, playback.state);
            playback.time += delta * target.speed;
            if (playback.transitionIndex != 0xffffffffu)
            {
                PoseStateDefinition source = ReadPoseState(machine, playback.sourceState);
                playback.sourceTime += delta * source.speed;
                playback.transitionTime += delta;
                float duration = asfloat(GpuPoseClips.Load(machine.y + playback.transitionIndex * 20u + 16u));
                playback.weight = min(playback.transitionTime / duration, 1.0);
                if (playback.weight == 1.0) playback.transitionIndex = 0xffffffffu;
            }
            if (playback.transitionIndex == 0xffffffffu)
            {
                playback.sourceState = playback.state;
                playback.sourceTime = playback.time;
                playback.weight = 1.0;
            }
            playback.sampleTime = PoseSampleTime(target, playback.time);
            playback.sourceSampleTime = PoseSampleTime(ReadPoseState(machine, playback.sourceState), playback.sourceTime);
            playback.finished = target.loop == 0u && playback.time >= target.duration && playback.weight == 1.0 ? 1u : 0u;
        }
    }
    playback.playbackTime = control.playbackTime;
    playback.starts = control.starts;
    playback.inputNumber = control.inputNumber;
    playback.calculationTime = control.calculationTime;
    GpuPosePlaybackStates[thread.x] = playback;
}

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

PoseTrs SampleClip(uint clip, float time, uint bone)
{
    uint4 header = GpuPoseClips.Load4(clip);
    float sample = clamp(time * asfloat(header.z), 0.0, float(header.y - 1u));
    uint first = uint(sample);
    PoseTrs a = DecodeTrs(clip, bone, first, header.x, header.w);
    PoseTrs b = DecodeTrs(clip, bone, min(first + 1u, header.y - 1u), header.x, header.w);
    float amount = sample - float(first);
    PoseTrs result;
    result.translation = lerp(a.translation, b.translation, amount);
    result.rotation = InterpolateRotation(a.rotation, b.rotation, amount);
    result.scale = lerp(a.scale, b.scale, amount);
    return result;
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
    GpuPosePlayback playback = GpuPosePlaybackStates[work.x];
    uint clip = control.clipOffset;
    uint4 machine = uint4(0u);
    if ((control.flags & 8u) != 0u)
    {
        machine = GpuPoseClips.Load4(control.machineOffset);
        clip = ReadPoseState(machine, playback.state).clipOffset;
    }
    PoseTrs pose = SampleClip(clip, playback.sampleTime, work.y);
    if ((control.flags & 8u) != 0u && playback.weight < 1.0)
    {
        PoseTrs source = SampleClip(ReadPoseState(machine, playback.sourceState).clipOffset, playback.sourceSampleTime, work.y);
        pose.translation = lerp(source.translation, pose.translation, playback.weight);
        pose.rotation = InterpolateRotation(source.rotation, pose.rotation, playback.weight);
        pose.scale = lerp(source.scale, pose.scale, playback.weight);
    }
    float4x4 local = TrsMatrix(pose.translation, pose.rotation, pose.scale);
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
