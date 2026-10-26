#include "gpu_pose_sampling.slang"
#include "gpu_pose_translation.slang"

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

struct GpuPoseGoal
{
    float3 position;
    float positionWeight;
    float3 bendDirection;
    float orientationWeight;
    float4 orientation;
};
StructuredBuffer<GpuPoseGoal> GpuPoseGoals;
struct GpuPoseMovement { float4 velocity; float4 animationVelocity; float4 facingDirection; };
StructuredBuffer<GpuPoseMovement> GpuPoseMovements;

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

void SolveTwoBoneGoal(uint definition, GpuPoseGoal goal)
{
    uint3 bones = GpuPoseClips.Load3(definition);
    if (goal.positionWeight > 0.0)
    {
        float3 a = GpuPoseModels[bones.x][3].xyz, b = GpuPoseModels[bones.y][3].xyz, c = GpuPoseModels[bones.z][3].xyz;
        float upper = length(b - a), lower = length(c - b);
        float3 target = lerp(c, goal.position, goal.positionWeight);
        float3 direction = normalize(target - a);
        float2 bend = asfloat(GpuPoseClips.Load2(definition + 16u));
        float minimum = sqrt(max(upper * upper + lower * lower + 2.0 * upper * lower * bend.y, 0.0));
        float maximum = sqrt(max(upper * upper + lower * lower + 2.0 * upper * lower * bend.x, 0.0));
        float distance = clamp(length(target - a), minimum, maximum);
        float along = (upper * upper - lower * lower + distance * distance) / (2.0 * distance);
        float height = sqrt(max(upper * upper - along * along, 0.0));
        float3 bendDirection = normalize(goal.bendDirection - direction * dot(goal.bendDirection, direction));
        float3 middle = a + direction * along + bendDirection * height;
        float3 tip = a + direction * distance;
        RotateModelDescendants(GpuPoseClips.Load2(definition + 32u), a, DirectionRotation(b - a, middle - a));
        b = GpuPoseModels[bones.y][3].xyz;
        c = GpuPoseModels[bones.z][3].xyz;
        RotateModelDescendants(GpuPoseClips.Load2(definition + 40u), b, DirectionRotation(c - b, tip - b));
    }
    if (goal.orientationWeight > 0.0)
    {
        float4 current = ModelRotation(GpuPoseModels[bones.z]);
        float4 rotation = InterpolateRotation(current, goal.orientation, goal.orientationWeight);
        float4 delta = MultiplyRotation(rotation, float4(-current.xyz, current.w));
        RotateModelDescendants(GpuPoseClips.Load2(definition + 48u), GpuPoseModels[bones.z][3].xyz, delta);
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
    SolveTwoBoneGoal(work.y, GpuPoseGoals[work.z]);
}

GpuPoseGoal StrideFootGoal(uint2 leg, float3 pivot, float3 direction, float scale, float weight, float3 pelvisOffset)
{
    GpuPoseGoal goal = GpuPoseGoals[leg.y];
    uint tip = GpuPoseClips.Load(leg.x + 8u);
    float4x4 model = GpuPoseModels[tip];
    float3 source = model[3].xyz - pelvisOffset;
    float3 extended = source + direction * (dot(source - pivot, direction) * (scale - 1.0));
    goal.position = lerp(source, lerp(extended, goal.position, goal.positionWeight), weight);
    goal.orientation = InterpolateRotation(ModelRotation(model), goal.orientation, goal.orientationWeight * weight);
    goal.positionWeight = 1.0;
    goal.orientationWeight = 1.0;
    return goal;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void WarpStride(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = GpuPoseWork[thread.x];
    if ((GpuPoseControls[work.x].flags & 1u) == 0u) return;
    GpuPoseMovement movement = GpuPoseMovements[work.x];
    float weight = movement.velocity.w;
    if (weight == 0.0) return;
    uint4 pelvis = GpuPoseClips.Load4(work.y);
    uint4 rows = GpuPoseClips.Load4(work.y + 16u);
    float4 upScale = asfloat(GpuPoseClips.Load4(work.y + 32u));
    float4 limits = asfloat(GpuPoseClips.Load4(work.y + 48u));
    float3 up = upScale.xyz;
    float3 facingAxis = asfloat(GpuPoseClips.Load3(work.y + 64u));
    uint facingBone = rows.z == 0u ? pelvis.x : GpuPoseClips.Load(rows.y + (rows.z - 1u) * 16u);
    float3 sourceFacing = mul(float4(facingAxis, 0), GpuPoseModels[facingBone]).xyz;
    sourceFacing = normalize(sourceFacing - up * dot(sourceFacing, up));
    float3 sourceVelocity = movement.animationVelocity.xyz - up * dot(movement.animationVelocity.xyz, up);
    float3 actualVelocity = movement.velocity.xyz - up * dot(movement.velocity.xyz, up);
    float sourceSpeed = length(sourceVelocity), actualSpeed = length(actualVelocity);
    float3 sourceDirection = sourceVelocity / sourceSpeed;
    float3 actualDirection = actualSpeed > 0.0 ? actualVelocity / actualSpeed : sourceDirection;
    float angle = atan2(dot(up, cross(sourceDirection, actualDirection)), dot(sourceDirection, actualDirection));
    angle = clamp(angle, -limits.y, limits.y) * weight;
    float4 rotation = float4(up * sin(angle * 0.5), cos(angle * 0.5));
    float3 pivot = GpuPoseModels[pelvis.x][3].xyz;
    RotateModelDescendants(pelvis.yz, pivot, rotation);
    float3 direction = mul(float4(sourceDirection, 0), TrsMatrix(float3(0), rotation, float3(1))).xyz;
    float scale = clamp(actualSpeed / sourceSpeed, upScale.w, limits.x);
    float minimum = -limits.z, maximum = limits.z, preferred = 0.0;
    for (uint foot = 0u; foot < pelvis.w; foot++)
    {
        uint2 leg = GpuPoseClips.Load2(rows.x + foot * 16u);
        uint3 bones = GpuPoseClips.Load3(leg.x);
        float3 a = GpuPoseModels[bones.x][3].xyz, b = GpuPoseModels[bones.y][3].xyz, c = GpuPoseModels[bones.z][3].xyz;
        GpuPoseGoal goal = StrideFootGoal(leg, pivot, direction, scale, weight, float3(0));
        float upper = length(b - a), lower = length(c - b);
        float2 bend = asfloat(GpuPoseClips.Load2(leg.x + 16u));
        float squaredSum = upper * upper + lower * lower, twiceProduct = 2.0 * upper * lower;
        float3 difference = a - goal.position;
        float height = dot(difference, up);
        float horizontalSquared = max(dot(difference, difference) - height * height, 0.0);
        float nearHeight = sqrt(max(squaredSum + twiceProduct * bend.y - horizontalSquared, 0.0));
        float farHeight = sqrt(max(squaredSum + twiceProduct * bend.x - horizontalSquared, 0.0));
        minimum = max(minimum, -height + (height >= 0.0 ? nearHeight : -farHeight));
        maximum = min(maximum, -height + (height >= 0.0 ? farHeight : -nearHeight));
        preferred += dot(goal.position - c, up);
    }
    float3 offset = up * clamp(preferred / float(pelvis.w), minimum, maximum);
    TranslateModelDescendants(pelvis.yz, offset);
    for (uint foot = 0u; foot < pelvis.w; foot++)
    {
        uint2 leg = GpuPoseClips.Load2(rows.x + foot * 16u);
        SolveTwoBoneGoal(leg.x, StrideFootGoal(leg, pivot, direction, scale, weight, offset));
    }
    float3 facing = normalize(movement.facingDirection.xyz - up * dot(movement.facingDirection.xyz, up));
    float facingAngle = atan2(dot(up, cross(sourceFacing, facing)), dot(sourceFacing, facing));
    float correction = clamp(facingAngle, -limits.y, limits.y) * weight - angle;
    for (uint joint = 0u; joint < rows.z; joint++)
    {
        uint4 spine = GpuPoseClips.Load4(rows.y + joint * 16u);
        float turn = correction * asfloat(spine.w) * 0.5;
        RotateModelDescendants(spine.yz, GpuPoseModels[spine.x][3].xyz, float4(up * sin(turn), cos(turn)));
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
