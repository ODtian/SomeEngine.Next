#include "gpu_pose_rig.slang"

StructuredBuffer<uint> GpuPoseSections;
RWStructuredBuffer<float4x4> GpuPosePrevious;

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
    GpuPosePrevious[thread.x] = section != 0xffffffffu && (GpuPoseControls[section].flags & 1025u) != 0
        ? current : GpuPoseHistoryPrevious[thread.x];
}

struct XpbdFrame { float4x4 modelToWorld; float4x4 worldToModel; float4 step; };
struct XpbdPoint { float4 position; float4 velocity; };
struct XpbdWork { float4x4 sourceModel; float4 previous; float4 constraints; };
StructuredBuffer<XpbdFrame> GpuXpbdFrames;
RWStructuredBuffer<XpbdPoint> GpuXpbdPoints;
RWStructuredBuffer<XpbdWork> GpuXpbdWork;

float XpbdInverseMass(uint definitions, uint point)
{
    return asfloat(GpuPoseClips.Load(definitions + point * 32u + 28u));
}

void ProjectXpbdLength(uint definitions, uint first, uint index, float alpha)
{
    XpbdPoint a = GpuXpbdPoints[first + index - 1u], b = GpuXpbdPoints[first + index];
    float3 difference = b.position.xyz - a.position.xyz;
    float distance = length(difference);
    float3 gradient = difference / distance;
    float wa = XpbdInverseMass(definitions, index - 1u), wb = XpbdInverseMass(definitions, index);
    XpbdWork work = GpuXpbdWork[first + index];
    float change = (-(distance - work.constraints.z) - alpha * work.constraints.x) / (wa + wb + alpha);
    work.constraints.x += change;
    a.position.xyz -= wa * gradient * change;
    b.position.xyz += wb * gradient * change;
    GpuXpbdPoints[first + index - 1u] = a;
    GpuXpbdPoints[first + index] = b;
    GpuXpbdWork[first + index] = work;
}

void ProjectXpbdBend(uint definitions, uint first, uint index, float alpha, float minimumCosine)
{
    XpbdPoint a = GpuXpbdPoints[first + index - 1u], b = GpuXpbdPoints[first + index], c = GpuXpbdPoints[first + index + 1u];
    float3 e0 = b.position.xyz - a.position.xyz, e1 = c.position.xyz - b.position.xyz;
    float l0 = length(e0), l1 = length(e1);
    float3 u = e0 / l0, v = e1 / l1;
    float cosine = dot(u, v), constraint = cosine - minimumCosine;
    XpbdWork work = GpuXpbdWork[first + index];
    if (constraint >= 0.0 && work.constraints.y == 0.0) return;
    float3 d0 = (v - cosine * u) / l0, d1 = (u - cosine * v) / l1;
    float3 ga = -d0, gb = d0 - d1, gc = d1;
    float wa = XpbdInverseMass(definitions, index - 1u), wb = XpbdInverseMass(definitions, index), wc = XpbdInverseMass(definitions, index + 1u);
    float change = (-constraint - alpha * work.constraints.y) / (wa * dot(ga, ga) + wb * dot(gb, gb) + wc * dot(gc, gc) + alpha);
    change = max(0.0, work.constraints.y + change) - work.constraints.y;
    work.constraints.y += change;
    a.position.xyz += wa * ga * change;
    b.position.xyz += wb * gb * change;
    c.position.xyz += wc * gc * change;
    GpuXpbdPoints[first + index - 1u] = a;
    GpuXpbdPoints[first + index] = b;
    GpuXpbdPoints[first + index + 1u] = c;
    GpuXpbdWork[first + index] = work;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SimulateXpbdChain(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 dispatch = GpuPoseWork[thread.x];
    if ((GpuPoseControls[dispatch.x].flags & 1u) == 0u) return;
    uint4 chain = GpuPoseClips.Load4(dispatch.y);
    float4 compliance = asfloat(GpuPoseClips.Load4(dispatch.y + 16u));
    float4 acceleration = asfloat(GpuPoseClips.Load4(dispatch.y + 32u));
    XpbdFrame frame = GpuXpbdFrames[dispatch.x];
    for (uint index = 0u; index < chain.y; index++)
    {
        uint bone = GpuPoseClips.Load(chain.x + index * 32u);
        XpbdWork work = (XpbdWork)0;
        work.sourceModel = GpuPoseModels[bone];
        float3 source = mul(float4(work.sourceModel[3].xyz, 1), frame.modelToWorld).xyz;
        if (index != 0u)
        {
            uint parent = GpuPoseClips.Load(chain.x + (index - 1u) * 32u);
            float3 rest = asfloat(GpuPoseClips.Load3(chain.x + index * 32u + 16u));
            work.constraints.z = length(mul(float4(rest, 0), mul(GpuPoseModels[parent], frame.modelToWorld)).xyz);
        }
        GpuXpbdWork[chain.z + index] = work;
        if (frame.step.y != 0.0)
        {
            XpbdPoint point = (XpbdPoint)0;
            point.position = float4(source, 0);
            GpuXpbdPoints[chain.z + index] = point;
        }
    }
    uint steps = uint(ceil(frame.step.x / acceleration.w));
    for (uint step = 0u; step < steps; step++)
    {
        float h = frame.step.x / float(steps);
        for (uint index = 0u; index < chain.y; index++)
        {
            XpbdPoint point = GpuXpbdPoints[chain.z + index];
            XpbdWork work = GpuXpbdWork[chain.z + index];
            work.previous = point.position;
            work.constraints.xy = float2(0);
            if (XpbdInverseMass(chain.x, index) == 0.0)
            {
                float3 attachment = mul(float4(work.sourceModel[3].xyz, 1), frame.modelToWorld).xyz;
                point.position.xyz = lerp(point.position.xyz, attachment, 1.0 / float(steps - step));
            }
            else point.position.xyz += h * exp(-compliance.w * h) * point.velocity.xyz + h * h * acceleration.xyz;
            GpuXpbdWork[chain.z + index] = work;
            GpuXpbdPoints[chain.z + index] = point;
        }
        float inverseH2 = 1.0 / (h * h);
        for (uint iteration = 0u; iteration < chain.w; iteration++)
        {
            for (uint index = 1u; index < chain.y; index++)
                ProjectXpbdLength(chain.x, chain.z, index, compliance.x * inverseH2);
            for (uint index = 1u; index + 1u < chain.y; index++)
                ProjectXpbdBend(chain.x, chain.z, index, compliance.y * inverseH2, compliance.z);
        }
        for (uint index = 0u; index < chain.y; index++)
        {
            XpbdPoint point = GpuXpbdPoints[chain.z + index];
            point.velocity.xyz = (point.position.xyz - GpuXpbdWork[chain.z + index].previous.xyz) / h;
            GpuXpbdPoints[chain.z + index] = point;
        }
    }
    for (uint index = 0u; index < chain.y; index++)
    {
        uint3 bone = GpuPoseClips.Load3(chain.x + index * 32u);
        uint a = index + 1u < chain.y ? index : index - 1u, b = a + 1u;
        float3 sourceDirection = GpuXpbdWork[chain.z + b].sourceModel[3].xyz - GpuXpbdWork[chain.z + a].sourceModel[3].xyz;
        float3 worldDirection = GpuXpbdPoints[chain.z + b].position.xyz - GpuXpbdPoints[chain.z + a].position.xyz;
        float3 direction = mul(float4(worldDirection, 0), frame.worldToModel).xyz;
        float4 swing = DirectionRotation(sourceDirection, direction);
        float4 targetRotation = ModelRotation(mul(GpuXpbdWork[chain.z + index].sourceModel, TrsMatrix(float3(0), swing, float3(1))));
        float4 currentRotation = ModelRotation(GpuPoseModels[bone.x]);
        float4 delta = MultiplyRotation(targetRotation, float4(-currentRotation.xyz, currentRotation.w));
        float3 current = GpuPoseModels[bone.x][3].xyz;
        float3 position = mul(float4(GpuXpbdPoints[chain.z + index].position.xyz, 1), frame.worldToModel).xyz;
        RotateModelDescendants(bone.yz, current, delta);
        TranslateModelDescendants(bone.yz, position - current);
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
    // Both an evaluated pose and a held XPBD pose advance the presented-frame history.
    if ((flags & 1025u) == 0) return;
    if ((flags & 2u) != 0) GpuPosePrevious[thread.x] = GpuPoseModels[thread.x];
    GpuPoseHistoryPrevious[thread.x] = GpuPosePrevious[thread.x];
    GpuPoseHistoryCurrent[thread.x] = GpuPoseModels[thread.x];
}
