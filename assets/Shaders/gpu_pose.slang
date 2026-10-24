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
    uint blendStart;
    uint referenceOffset;
    float presentationAmount;
    uint2 padding;
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
struct GpuRootCorrection { float4 translation; float4 rotation; };
StructuredBuffer<GpuRootCorrection> GpuPoseRoots;

struct GpuBlendState
{
    float phase;
    uint3 samples;
    float3 weights;
    float padding;
};

struct GpuBlendLocal { float4 translation; float4 rotation; float4 scale; };
RWStructuredBuffer<GpuBlendState> GpuBlendStates;
RWStructuredBuffer<GpuBlendLocal> GpuBlendLocals;

float2 BlendCoordinate(uint points, uint index)
{
    return asfloat(GpuPoseClips.Load2(points + index * 24u));
}

void ProjectBlendEdge(float2 coordinate, uint points, uint a, uint b,
    inout float nearest, inout GpuBlendState selection)
{
    float2 origin = BlendCoordinate(points, a), edge = BlendCoordinate(points, b) - origin;
    float amount = saturate(dot(coordinate - origin, edge) / dot(edge, edge));
    float2 difference = coordinate - origin - edge * amount;
    float distance = dot(difference, difference);
    if (distance < nearest)
    {
        nearest = distance;
        selection.samples = uint3(a, b, a);
        selection.weights = float3(1.0 - amount, amount, 0.0);
    }
}

GpuBlendState SelectBlendSpace(uint space, float2 coordinate)
{
    uint4 ranges = GpuPoseClips.Load4(space);
    uint4 triangles = GpuPoseClips.Load4(space + 16u);
    GpuBlendState selection = (GpuBlendState)0;
    if (ranges.w != 0u)
    {
        uint interval = 0u;
        while (interval + 1u < ranges.w)
        {
            uint b = GpuPoseClips.Load(ranges.z + interval * 16u + 4u);
            if (coordinate.x <= BlendCoordinate(ranges.x, b).x) break;
            interval++;
        }
        uint4 span = GpuPoseClips.Load4(ranges.z + interval * 16u);
        float amount = saturate((coordinate.x - asfloat(span.z)) * asfloat(span.w));
        selection.samples = uint3(span.x, span.y, span.x);
        selection.weights = float3(1.0 - amount, amount, 0.0);
        return selection;
    }
    for (uint triangle = 0u; triangle < triangles.y; triangle++)
    {
        uint3 indices = GpuPoseClips.Load3(triangles.x + triangle * 12u);
        float2 origin = BlendCoordinate(ranges.x, indices.x);
        float2 b = BlendCoordinate(ranges.x, indices.y) - origin;
        float2 c = BlendCoordinate(ranges.x, indices.z) - origin;
        float2 relative = coordinate - origin;
        float inverse = 1.0 / (b.x * c.y - c.x * b.y);
        float wb = dot(relative, float2(c.y, -c.x)) * inverse;
        float wc = dot(relative, float2(-b.y, b.x)) * inverse;
        float wa = 1.0 - wb - wc;
        if (wa < 0.0 || wb < 0.0 || wc < 0.0) continue;
        selection.samples = indices;
        selection.weights = float3(wa, wb, wc);
        return selection;
    }
    float nearest = 3.402823466e+38;
    for (uint triangle = 0u; triangle < triangles.y; triangle++)
    {
        uint3 indices = GpuPoseClips.Load3(triangles.x + triangle * 12u);
        ProjectBlendEdge(coordinate, ranges.x, indices.x, indices.y, nearest, selection);
        ProjectBlendEdge(coordinate, ranges.x, indices.y, indices.z, nearest, selection);
        ProjectBlendEdge(coordinate, ranges.x, indices.z, indices.x, nearest, selection);
    }
    return selection;
}

void AdvanceBlendInputs(GpuPoseControl control, bool reset, float delta)
{
    uint4 graph = GpuPoseClips.Load4(control.machineOffset);
    for (uint index = 0u; index < graph.y; index++)
    {
        uint address = graph.x + index * 32u;
        uint4 node = GpuPoseClips.Load4(address), operation = GpuPoseClips.Load4(address + 16u);
        GpuBlendState prior = GpuBlendStates[control.blendStart + index];
        float phase = reset ? 0.0 : prior.phase;
        GpuBlendState selection = (GpuBlendState)0;
        if (node.x == 0u) phase += delta * asfloat(operation.z);
        else if (node.x == 1u)
        {
            uint4 header = GpuPoseClips.Load4(node.y);
            uint4 details = GpuPoseClips.Load4(node.y + 16u);
            float2 coordinate = float2(GpuPoseInputs[control.inputOffset + details.z], GpuPoseInputs[control.inputOffset + details.w]);
            selection = SelectBlendSpace(node.y, coordinate);
            float frequency = 0.0;
            for (uint sample = 0u; sample < 3u; sample++)
                frequency += selection.weights[sample] / asfloat(GpuPoseClips.Load(header.x + selection.samples[sample] * 24u + 12u));
            phase += delta * frequency;
        }
        selection.phase = phase;
        GpuBlendStates[control.blendStart + index] = selection;
    }
}

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
    if ((control.flags & 128u) == 0u) return;
    GpuPosePlayback playback = GpuPosePlaybackStates[thread.x];
    bool reset = (control.flags & 34u) != 0u || playback.starts != control.starts
        || control.playbackTime < playback.playbackTime;
    if (!reset && all(playback.inputNumber == control.inputNumber)) return;
    if ((control.flags & 64u) != 0u)
    {
        float delta = (control.flags & 16u) == 0u ? 0.0 : control.playbackTime - (reset ? 0.0 : playback.playbackTime);
        AdvanceBlendInputs(control, reset, delta);
        playback = (GpuPosePlayback)0;
        playback.state = playback.sourceState = playback.transitionIndex = 0xffffffffu;
        playback.time = playback.sourceTime = control.playbackTime;
        playback.sampleTime = playback.sourceSampleTime = control.playbackTime;
        playback.weight = 1.0;
    }
    else if ((control.flags & 8u) == 0u)
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

PoseTrs ReadBlendLocal(uint index)
{
    GpuBlendLocal stored = GpuBlendLocals[index];
    PoseTrs result;
    result.translation = stored.translation.xyz;
    result.rotation = stored.rotation;
    result.scale = stored.scale.xyz;
    return result;
}

float4 MultiplyRotation(float4 a, float4 b)
{
    return float4(a.w * b.xyz + b.w * a.xyz + cross(a.xyz, b.xyz), a.w * b.w - dot(a.xyz, b.xyz));
}

PoseTrs SampleBlendSpace(uint space, GpuBlendState selection, uint bone)
{
    uint points = GpuPoseClips.Load(space);
    selection.weights /= selection.weights.x + selection.weights.y + selection.weights.z;
    uint first = 0xffffffffu;
    for (uint sample = 0u; sample < 3u; sample++)
        if (selection.weights[sample] > 0.0) first = min(first, selection.samples[sample]);
    uint4 point = GpuPoseClips.Load4(points + first * 24u);
    uint clip = GpuPoseClips.Load(points + first * 24u + 16u);
    float time = frac(selection.phase + asfloat(point.z)) * asfloat(point.w);
    float4 reference = SampleClip(clip, time, bone).rotation;
    PoseTrs result = (PoseTrs)0;
    for (uint sample = 0u; sample < 3u; sample++)
    {
        float weight = selection.weights[sample];
        if (weight == 0.0) continue;
        uint address = points + selection.samples[sample] * 24u;
        uint4 pointData = GpuPoseClips.Load4(address);
        uint pointClip = GpuPoseClips.Load(address + 16u);
        PoseTrs pose = SampleClip(pointClip, frac(selection.phase + asfloat(pointData.z)) * asfloat(pointData.w), bone);
        result.translation += pose.translation * weight;
        result.scale += pose.scale * weight;
        result.rotation += pose.rotation * (dot(reference, pose.rotation) < 0.0 ? -weight : weight);
    }
    result.rotation = normalize(result.rotation);
    return result;
}

PoseTrs SampleBlendGraph(GpuPoseControl control, uint bone)
{
    uint4 graph = GpuPoseClips.Load4(control.machineOffset);
    PoseTrs result = (PoseTrs)0;
    // Each invocation owns one bone's rows, so its dependency-ordered writes feed its later steps directly.
    for (uint index = 0u; index < graph.y; index++)
    {
        uint address = graph.x + index * 32u;
        uint4 node = GpuPoseClips.Load4(address), operation = GpuPoseClips.Load4(address + 16u);
        GpuBlendState selection = GpuBlendStates[control.blendStart + index];
        if (node.x == 0u)
        {
            uint4 clip = GpuPoseClips.Load4(node.y);
            float duration = clip.y > 1u ? float(clip.y - 1u) / asfloat(clip.z) : 0.0;
            float time = operation.y != 0u ? fmod(selection.phase, duration) : min(selection.phase, duration);
            result = SampleClip(node.y, time, bone);
        }
        else if (node.x == 1u) result = SampleBlendSpace(node.y, selection, bone);
        else
        {
            PoseTrs basis = ReadBlendLocal(control.clipOffset + node.z * graph.z + bone);
            PoseTrs source = ReadBlendLocal(control.clipOffset + node.w * graph.z + bone);
            float weight = asfloat(operation.z) * asfloat(GpuPoseClips.Load(operation.x + bone * 4u));
            if (operation.w != 0xffffffffu) weight *= GpuPoseInputs[control.inputOffset + operation.w];
            if (operation.y == 0u)
            {
                result.translation = lerp(basis.translation, source.translation, weight);
                result.scale = lerp(basis.scale, source.scale, weight);
                float4 rotation = dot(basis.rotation, source.rotation) < 0.0 ? -source.rotation : source.rotation;
                result.rotation = normalize(lerp(basis.rotation, rotation, weight));
            }
            else
            {
                uint reference = node.y + bone * 48u;
                float3 translation = asfloat(GpuPoseClips.Load3(reference));
                float4 rotation = asfloat(GpuPoseClips.Load4(reference + 16u));
                float3 scale = asfloat(GpuPoseClips.Load3(reference + 32u));
                float4 delta = MultiplyRotation(source.rotation, float4(-rotation.xyz, rotation.w));
                result.translation = basis.translation + (source.translation - translation) * weight;
                result.scale = basis.scale * lerp(float3(1.0), source.scale / scale, weight);
                result.rotation = normalize(MultiplyRotation(InterpolateRotation(float4(0, 0, 0, 1), delta, weight), basis.rotation));
            }
        }
        GpuBlendLocal stored;
        stored.translation = float4(result.translation, 0);
        stored.rotation = result.rotation;
        stored.scale = float4(result.scale, 0);
        GpuBlendLocals[control.clipOffset + index * graph.z + bone] = stored;
    }
    return result;
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
    uint reference = control.referenceOffset + work.y * 64u;
    uint selection = GpuPoseClips.Load(reference + 48u);
    PoseTrs pose;
    pose.translation = asfloat(GpuPoseClips.Load3(reference));
    pose.rotation = asfloat(GpuPoseClips.Load4(reference + 16u));
    pose.scale = asfloat(GpuPoseClips.Load3(reference + 32u));
    if ((selection & 1u) != 0u)
    {
        uint bones, stride;
        GpuPoseModels.GetDimensions(bones, stride);
        uint history = bones + work.w;
        if ((control.flags & 256u) != 0u)
        {
            if ((control.flags & 64u) != 0u) pose = SampleBlendGraph(control, work.y);
            else pose = SampleClip(clip, playback.sampleTime, work.y);
            if ((control.flags & 8u) != 0u && playback.weight < 1.0)
            {
                PoseTrs source = SampleClip(ReadPoseState(machine, playback.sourceState).clipOffset, playback.sourceSampleTime, work.y);
                pose.translation = lerp(source.translation, pose.translation, playback.weight);
                pose.rotation = InterpolateRotation(source.rotation, pose.rotation, playback.weight);
                pose.scale = lerp(source.scale, pose.scale, playback.weight);
            }
            GpuRootCorrection correction = GpuPoseRoots[work.x];
            if ((selection & 2u) != 0u && correction.translation.w != 0.0)
            {
                pose.translation = mul(float4(pose.translation, 1), TrsMatrix(correction.translation.xyz, correction.rotation, float3(1))).xyz;
                pose.rotation = MultiplyRotation(correction.rotation, pose.rotation);
            }
            float4x4 stored = float4x4(float4(pose.translation, 0), pose.rotation, float4(pose.scale, 0), float4(playback.sampleTime, 0, 0, 0));
            GpuPoseHistoryPrevious[history] = (control.flags & 512u) != 0u ? stored : GpuPoseHistoryCurrent[history];
            GpuPoseHistoryCurrent[history] = stored;
        }
        float4x4 before = GpuPoseHistoryPrevious[history], after = GpuPoseHistoryCurrent[history];
        pose.translation = lerp(before[0].xyz, after[0].xyz, control.presentationAmount);
        pose.rotation = InterpolateRotation(before[1], after[1], control.presentationAmount);
        pose.scale = lerp(before[2].xyz, after[2].xyz, control.presentationAmount);
    }
    float4x4 local = TrsMatrix(pose.translation, pose.rotation, pose.scale);
    GpuPoseModels[work.w] = work.z == 0xffffffffu ? local : mul(local, GpuPoseModels[work.z]);
}

struct GpuPoseGoal
{
    float3 position;
    float positionWeight;
    float3 bendDirection;
    float orientationWeight;
    float4 orientation;
};
StructuredBuffer<GpuPoseGoal> GpuPoseGoals;
StructuredBuffer<float4> GpuPoseOffsets;

float4 DirectionRotation(float3 from, float3 to)
{
    from = normalize(from); to = normalize(to);
    float cosine = clamp(dot(from, to), -1.0, 1.0);
    float3 axis = cross(from, to);
    float sine = length(axis);
    if (sine == 0.0)
    {
        if (cosine >= 0.0) return float4(0, 0, 0, 1);
        axis = normalize(abs(from.x) > abs(from.z) ? float3(-from.y, from.x, 0) : float3(0, -from.z, from.y));
        return float4(axis, 0);
    }
    float halfAngle = atan2(sine, cosine) * 0.5;
    return float4(axis / sine * sin(halfAngle), cos(halfAngle));
}

float4 ModelRotation(float4x4 model)
{
    float3 x = normalize(model[0].xyz), y = normalize(model[1].xyz), z = normalize(model[2].xyz);
    float trace = x.x + y.y + z.z;
    float4 rotation;
    if (trace > 0.0)
    {
        float scale = sqrt(trace + 1.0);
        rotation = float4(float3(y.z - z.y, z.x - x.z, x.y - y.x) * (0.5 / scale), scale * 0.5);
    }
    else if (x.x >= y.y && x.x >= z.z)
    {
        float scale = sqrt(1.0 + x.x - y.y - z.z), inverse = 0.5 / scale;
        rotation = float4(scale * 0.5, (x.y + y.x) * inverse, (x.z + z.x) * inverse, (y.z - z.y) * inverse);
    }
    else if (y.y > z.z)
    {
        float scale = sqrt(1.0 + y.y - x.x - z.z), inverse = 0.5 / scale;
        rotation = float4((y.x + x.y) * inverse, scale * 0.5, (y.z + z.y) * inverse, (z.x - x.z) * inverse);
    }
    else
    {
        float scale = sqrt(1.0 + z.z - x.x - y.y), inverse = 0.5 / scale;
        rotation = float4((z.x + x.z) * inverse, (z.y + y.z) * inverse, scale * 0.5, (x.y - y.x) * inverse);
    }
    return normalize(rotation);
}

void RotateModelDescendants(uint2 range, float3 pivot, float4 rotation)
{
    float4x4 turn = TrsMatrix(float3(0), rotation, float3(1));
    for (uint index = 0u; index < range.y; index++)
    {
        uint bone = GpuPoseClips.Load(range.x + index * 4u);
        float4x4 model = GpuPoseModels[bone];
        model[3].xyz -= pivot;
        model = mul(model, turn);
        model[3].xyz += pivot;
        GpuPoseModels[bone] = model;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void TranslatePose(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = GpuPoseWork[thread.x];
    if ((GpuPoseControls[work.x].flags & 1u) == 0u) return;
    uint2 range = GpuPoseClips.Load2(work.y);
    float3 offset = GpuPoseOffsets[work.x].xyz;
    for (uint index = 0u; index < range.y; index++)
    {
        uint bone = GpuPoseClips.Load(range.x + index * 4u);
        float4x4 model = GpuPoseModels[bone];
        model[3].xyz += offset;
        GpuPoseModels[bone] = model;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SolveTwoBone(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = GpuPoseWork[thread.x];
    if ((GpuPoseControls[work.x].flags & 1u) == 0u) return;
    uint3 bones = GpuPoseClips.Load3(work.y);
    GpuPoseGoal goal = GpuPoseGoals[work.z];
    if (goal.positionWeight > 0.0)
    {
        float3 a = GpuPoseModels[bones.x][3].xyz, b = GpuPoseModels[bones.y][3].xyz, c = GpuPoseModels[bones.z][3].xyz;
        float upper = length(b - a), lower = length(c - b);
        float3 target = lerp(c, goal.position, goal.positionWeight);
        float3 direction = normalize(target - a);
        float2 bend = asfloat(GpuPoseClips.Load2(work.y + 16u));
        float minimum = sqrt(max(upper * upper + lower * lower + 2.0 * upper * lower * bend.y, 0.0));
        float maximum = sqrt(max(upper * upper + lower * lower + 2.0 * upper * lower * bend.x, 0.0));
        float distance = clamp(length(target - a), minimum, maximum);
        float along = (upper * upper - lower * lower + distance * distance) / (2.0 * distance);
        float height = sqrt(max(upper * upper - along * along, 0.0));
        float3 bendDirection = normalize(goal.bendDirection - direction * dot(goal.bendDirection, direction));
        float3 middle = a + direction * along + bendDirection * height;
        float3 tip = a + direction * distance;
        RotateModelDescendants(GpuPoseClips.Load2(work.y + 32u), a, DirectionRotation(b - a, middle - a));
        b = GpuPoseModels[bones.y][3].xyz;
        c = GpuPoseModels[bones.z][3].xyz;
        RotateModelDescendants(GpuPoseClips.Load2(work.y + 40u), b, DirectionRotation(c - b, tip - b));
    }
    if (goal.orientationWeight > 0.0)
    {
        float4 current = ModelRotation(GpuPoseModels[bones.z]);
        float4 rotation = InterpolateRotation(current, goal.orientation, goal.orientationWeight);
        float4 delta = MultiplyRotation(rotation, float4(-current.xyz, current.w));
        RotateModelDescendants(GpuPoseClips.Load2(work.y + 48u), GpuPoseModels[bones.z][3].xyz, delta);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SolveAim(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = GpuPoseWork[thread.x];
    if ((GpuPoseControls[work.x].flags & 1u) == 0u) return;
    GpuPoseGoal goal = GpuPoseGoals[work.z];
    if (goal.positionWeight == 0.0) return;
    uint2 chain = GpuPoseClips.Load2(work.y);
    for (uint joint = 0u; joint < chain.y; joint++)
    {
        uint address = chain.x + joint * 48u;
        uint3 bone = GpuPoseClips.Load3(address);
        float4 axis = asfloat(GpuPoseClips.Load4(address + 16u));
        float maximum = asfloat(GpuPoseClips.Load(address + 32u));
        float4x4 model = GpuPoseModels[bone.x];
        float3 current = normalize(mul(float4(axis.xyz, 0), model).xyz);
        float3 direction = normalize(goal.position - model[3].xyz);
        float angle = acos(clamp(dot(current, direction), -1.0, 1.0));
        if (angle > 0.0)
        {
            float weight = min(angle, maximum) / angle * axis.w * goal.positionWeight;
            float4 delta = InterpolateRotation(float4(0, 0, 0, 1), DirectionRotation(current, direction), weight);
            RotateModelDescendants(bone.yz, model[3].xyz, delta);
        }
    }
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
