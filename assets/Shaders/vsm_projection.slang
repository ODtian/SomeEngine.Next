#ifndef VSM_PROJECTION_SLANG
#define VSM_PROJECTION_SLANG

#include "depth_projection.slang"

#include "vsm_sampling.slang"
#include "vsm_receiver_projection.slang"

struct VirtualShadowProjectionResult
{
    bool Valid;
    float Visibility;
    float OccluderDistance;
    uint ViewIndex;
    uint MipLevel;
    uint RayCount;
};

VirtualShadowProjectionResult EmptyVirtualShadowProjection()
{
    VirtualShadowProjectionResult result = (VirtualShadowProjectionResult)0;
    result.Visibility = 1.0;
    result.OccluderDistance = -1.0;
    return result;
}

struct VirtualShadowDepthSample
{
    // Depth and sample coordinates all belong to the actual atlas source view.
    bool Valid;
    float Depth;
    uint ViewIndex;
    uint MipLevel;
    float2 UV;
    float2 TexelCenter;
};

bool IsDirectionalVirtualShadowProjection(VirtualShadowMapParameters view)
{
#ifdef VSM_PROJECT_DIRECTIONAL_LIGHTS
    return VSM_PROJECT_DIRECTIONAL_LIGHTS != 0;
#else
    return view.LightKind == VSM_LIGHT_DIRECTIONAL;
#endif
}

float VirtualShadowDepthRoundingRadius(float depth)
{
    float magnitude = abs(depth);
    uint bits = asuint(magnitude);
    float previous = asfloat(bits == 0u ? 0u : bits - 1u);
    float next = asfloat(bits + 1u);
    return 0.5 * max(magnitude - previous, next - magnitude);
}

float VirtualShadowReverseDepthRoundingRadius(float reverseDepth)
{
    // Legacy PCSS tolerance retained while hard projection uses native reverse-Z.
    // Its conservative forward/reverse bound is not used by the hard comparison.
    return VirtualShadowDepthRoundingRadius(1.0 - reverseDepth)
        + VirtualShadowDepthRoundingRadius(reverseDepth);
}

float CalculateVirtualShadowDirectionalDepthOffset(VirtualShadowMapParameters view, float3 position)
{
    float3 viewPosition = position + view.WorldTranslation;
    return dot(viewPosition, float3(view.ShadowUv[0][2], view.ShadowUv[1][2], view.ShadowUv[2][2]))
        + (view.ShadowUv[3][2] - 0.5);
}

VirtualShadowDepthSample ReadVirtualShadowProjectionDepth(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pages,
    Texture2DArray<uint> atlas, uint requestedView, float2 uv, uint firstMip)
{
    VirtualShadowDepthSample sample = (VirtualShadowDepthSample)0;
    sample.ViewIndex = requestedView;
    VirtualShadowMapParameters view = views[requestedView];
    float reverseDepth;
    if (IsDirectionalVirtualShadowProjection(view))
    {
#ifdef VSM_RESIDENT_LOOKUP_ENABLED
        sample.Valid = TryReadVirtualShadowDirectionalResidentSourceReverseDepth(views, pages,
            VirtualShadowResidentPages, atlas, requestedView, uv, reverseDepth, sample.ViewIndex);
#else
        uint remaining = view.ClipmapLevelCount - uint(view.ClipmapLevel - view.FirstClipmapLevel);
        for (uint offset = 0u; offset < remaining; offset++)
        {
            VirtualShadowMapParameters source = views[requestedView + offset];
            float4 transform = VirtualShadowClipmapScaleBias(view, source, offset);
            float2 sourceUV = uv * transform.x + transform.yz;
            if (any(sourceUV < 0.0) || any(sourceUV >= 1.0)) continue;
            uint2 pixel = uint2(sourceUV * float(source.VirtualResolution));
            if (!TryReadVirtualShadowMipReverseDepth(source, pages, atlas, 0u, pixel, reverseDepth)) continue;
            sample.ViewIndex = requestedView + offset;
            sample.Valid = true;
            break;
        }

#endif
        if (!sample.Valid) return sample;
        VirtualShadowMapParameters source = views[sample.ViewIndex];
        float4 transform = VirtualShadowClipmapScaleBias(view, source, sample.ViewIndex - requestedView);
        float2 sourceUV = uv * transform.x + transform.yz;
        uint2 basePage = min(uint2(uv * float(VirtualShadowPagesPerRow(view))), VirtualShadowPagesPerRow(view) - 1u);
        uint2 sourcePage = uint2(VirtualShadowCoarseClipmapPage(basePage, view, source,
            sample.ViewIndex - requestedView));
        uint2 pixel = clamp(uint2(max(sourceUV, 0.0.xx) * float(source.VirtualResolution)),
            sourcePage * source.PageSize, (sourcePage + 1u) * source.PageSize - 1u);
        sample.UV = sourceUV;
        sample.TexelCenter = (float2(pixel) + 0.5) / float(source.VirtualResolution);
    }
    else
    {
        sample.Valid = TryReadVirtualShadowReverseDepthAtOrCoarser(view, pages, atlas, firstMip, uv,
            reverseDepth, sample.MipLevel);
        if (!sample.Valid) return sample;
        uint resolution = VirtualShadowMipResolution(view, sample.MipLevel);
        uint2 pixel = min(uint2(uv * float(resolution)), resolution - 1u);
        sample.UV = uv;
        sample.TexelCenter = (float2(pixel) + 0.5) / float(resolution);
    }
    sample.Depth = reverseDepth;
    return sample;
}

float VirtualShadowOptimalSlopeBias(StructuredBuffer<VirtualShadowMapParameters> views,
    uint requestedView, VirtualShadowDepthSample sample, float3 position, float3 normal)
{
    VirtualShadowMapParameters source = views[sample.ViewIndex];
    float3 ndc;
    float2 uv;
    ProjectVirtualShadowPosition(source, position, ndc, uv);
    float2 slope = -CalculateVirtualShadowReceiverPlaneDepthGradient(source, position, normal);
    float bias = 2.0 * max(0.0, dot(slope, sample.TexelCenter - sample.UV));
    float depthScale = IsDirectionalVirtualShadowProjection(source)
        ? abs(source.ProjectionDepthScale) : abs(1.0 + source.ProjectionDepthScale);
    bias = min(bias, 100.0 * (IsDirectionalVirtualShadowProjection(source) ? source.WorldUnitsPerCentimeter : 1.0) * depthScale);
    if (IsDirectionalVirtualShadowProjection(source))
        bias *= exp2(float(sample.ViewIndex - requestedView));
    return bias;
}

float VirtualShadowOccluderDistance(VirtualShadowMapParameters view,
    float occluderDepth, float receiverDepth, float receiverDistance)
{
    float minimumDistance = 1e-6 * view.WorldUnitsPerCentimeter;
    if (IsDirectionalVirtualShadowProjection(view))
        return max(minimumDistance, (occluderDepth - receiverDepth) / abs(view.ProjectionDepthScale));
    float occluderViewDistance = view.ProjectionDepthOffset /
        (1.0 - occluderDepth + view.ProjectionDepthScale);
    float receiverViewDistance = view.ProjectionDepthOffset /
        (1.0 - receiverDepth + view.ProjectionDepthScale);
    return max(minimumDistance, receiverDistance * (1.0 - occluderViewDistance / receiverViewDistance));
}

float CalculateVirtualShadowRayStartBias(VirtualShadowMapParameters source, float3 position,
    float rayStartDistance)
{
    float depthScale = IsDirectionalVirtualShadowProjection(source)
        ? abs(source.ProjectionDepthScale)
        : abs(1.0 + source.ProjectionDepthScale) / ProjectVirtualShadowClip(source, position).w;
    return max(rayStartDistance, 0.0) * depthScale;
}

VirtualShadowProjectionResult ProjectVirtualShadowHard(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pages,
    Texture2DArray<uint> atlas, uint viewIndex, float3 position, float3 normal,
    uint firstMip, float rayStartDistance)
{
    VirtualShadowProjectionResult result = EmptyVirtualShadowProjection();
    VirtualShadowMapParameters view = views[viewIndex];
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(view, position, ndc, uv)) return result;
    VirtualShadowDepthSample sample = ReadVirtualShadowProjectionDepth(views, pages, atlas, viewIndex, uv, firstMip);
    if (!sample.Valid) return result;
    result.Valid = true;
    result.ViewIndex = sample.ViewIndex;
    result.MipLevel = sample.MipLevel;
    result.RayCount = 1u;
    VirtualShadowMapParameters source = views[sample.ViewIndex];
    float nativeSlopeBias = VirtualShadowOptimalSlopeBias(views, sample.ViewIndex, sample, position, normal);
    float rayBias = CalculateVirtualShadowRayStartBias(source, position, rayStartDistance);
    float biasedDepth = sample.Depth - nativeSlopeBias - rayBias;
    float4 receiverPosition = float4(position + source.WorldTranslation, 1.0);
    float receiverDepth = ProjectReverseDepth(receiverPosition, source.LightViewProjection)
        / ProjectVirtualShadowClip(source, position).w;
    bool blocked = biasedDepth > receiverDepth;
    if (blocked)
    {
        result.Visibility = 0.0;
        result.OccluderDistance = IsDirectionalVirtualShadowProjection(source)
            ? max(1e-6 * source.WorldUnitsPerCentimeter, (sample.Depth - receiverDepth) / abs(source.ProjectionDepthScale))
            : VirtualShadowOccluderDistance(source, biasedDepth, receiverDepth, length(position - source.LightPosition));
    }
    return result;
}

struct VirtualShadowRaySettings
{
    uint RayCount;
    uint SamplesPerRay;
    uint AdaptiveRayCount;
    float ExtrapolateMaxSlope;
    float TexelDitherScale;
    float RayLengthScale;
    float MaxRayAngle;
    float MaxSlopeBias;
};

struct VirtualShadowRay
{
    uint FirstView;
    uint SecondView;
    uint MinMip;
    float3 FirstStart;
    float3 FirstStep;
    float3 SecondStart;
    float3 SecondStep;
    float FirstSlope;
    float SecondSlope;
    bool UseSecondFace;
    bool AnyValid;
    uint SampledView;
    uint SampledMip;
};

struct VirtualShadowRaySample
{
    bool Valid;
    float Depth;
    float ReferenceDepth;
    float MaxSlope;
    bool ResetSlope;
};

float3 ProjectVirtualShadowRayPoint(VirtualShadowMapParameters view, float3 position)
{
    float4 clip = ProjectVirtualShadowClip(view, position);
    float3 ndc = clip.xyz / clip.w;
    return float3(ndc.xy * float2(.5, -.5) + .5, 1.0 - ndc.z);
}

VirtualShadowRaySample ReadVirtualShadowRayFace(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, inout VirtualShadowRay ray, float time)
{
    VirtualShadowRaySample result = (VirtualShadowRaySample)0;
    uint viewIndex = ray.UseSecondFace ? ray.SecondView : ray.FirstView;
    float3 start = ray.UseSecondFace ? ray.SecondStart : ray.FirstStart;
    float3 step = ray.UseSecondFace ? ray.SecondStep : ray.FirstStep;
    float3 uvDepth = start + step * time;
    result.ReferenceDepth = uvDepth.z;
    result.MaxSlope = ray.UseSecondFace ? ray.SecondSlope : ray.FirstSlope;
    if (all(uvDepth.xy >= 0.0) && all(uvDepth.xy < 1.0))
    {
        VirtualShadowDepthSample depth = ReadVirtualShadowProjectionDepth(views, pages, atlas,
            viewIndex, uvDepth.xy, ray.MinMip);
        result.Valid = depth.Valid;
        result.Depth = depth.Depth;
        if (depth.Valid && depth.ViewIndex != viewIndex && IsDirectionalVirtualShadowProjection(views[viewIndex]))
        {
            float4 transform = VirtualShadowClipmapScaleBias(views[viewIndex], views[depth.ViewIndex],
                depth.ViewIndex - viewIndex);
            result.Depth = VirtualShadowReverseDepthInRequestedClipmap(result.Depth, transform);
        }
        if (depth.Valid)
        {
            ray.AnyValid = true;
            ray.SampledView = depth.ViewIndex;
            ray.SampledMip = depth.MipLevel;
        }
    }
    return result;
}

VirtualShadowRaySample ReadVirtualShadowRaySample(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, inout VirtualShadowRay ray, float time)
{
    VirtualShadowRaySample result = ReadVirtualShadowRayFace(views, pages, atlas, ray, time);
    if (!result.Valid && ray.UseSecondFace)
    {
        ray.UseSecondFace = false;
        result = ReadVirtualShadowRayFace(views, pages, atlas, ray, time);
        result.ResetSlope = true;
    }
    return result;
}

bool TraceVirtualShadowRay(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, inout VirtualShadowRay ray,
    uint steps, float stepOffset, out float hitDepth)
{
    float historyDepth = -10000.0;
    float historyTime = -1.0;
    float historySlope = 0.0;
    float previousReference = -1.0;
    hitDepth = -1.0;
    for (uint index = 0u; index <= steps; index++)
    {
        float time = index == steps ? 0.0 : pow(1.0 - (float(index) + 1.0 - stepOffset) / float(steps), 2.0);
        VirtualShadowRaySample value = ReadVirtualShadowRaySample(views, pages, atlas, ray, time);
        if (value.ResetSlope) historySlope = value.MaxSlope;
        if (!value.Valid) continue;
        if (historyDepth == -10000.0)
        {
            historyDepth = value.Depth;
            historyTime = time;
            if (value.Depth > value.ReferenceDepth) { hitDepth = value.Depth; return true; }
        }
        else
        {
            float tolerance = 1.05 * abs(value.ReferenceDepth - previousReference);
            float deltaTime = time - historyTime;
            bool behind = value.Depth - value.ReferenceDepth > tolerance;
            float compareDepth = value.Depth;
            if (behind) compareDepth = historyDepth + historySlope * deltaTime;
            else if (value.Depth != historyDepth)
            {
                historySlope = clamp((value.Depth - historyDepth) / deltaTime, -abs(value.MaxSlope), abs(value.MaxSlope));
                historyDepth = value.Depth;
                historyTime = time;
            }
            if (abs(value.ReferenceDepth - compareDepth + .5 * tolerance) < .5 * tolerance)
            {
                hitDepth = compareDepth;
                return true;
            }
        }
        previousReference = value.ReferenceDepth;
    }
    return false;
}

float2 VirtualShadowConcentricDisk(float2 random)
{
    float2 square = 2.0 * random - .99999994;
    float2 magnitude = abs(square);
    float smaller = min(magnitude.x, magnitude.y);
    float radius = max(magnitude.x, magnitude.y);
    float angle = .7853981634 * (smaller / (radius + 5.42101086243e-20) +
        2.0 * float(magnitude.y >= magnitude.x));
    float2 circle = asfloat((asuint(float2(cos(angle), sin(angle))) & 0x7fffffffu) |
        (asuint(square) & 0x80000000u));
    return radius * circle;
}

float4 VirtualShadowRayRandom(uint2 pixel, uint frameIndex, uint rayIndex, uint rayCount)
{
    float2 offset = frac(float(rayIndex) * float2(.754877669, .569840296));
    float2 secondOffset = frac(float(rayIndex + rayCount) * float2(.754877669, .569840296));
#ifdef VSM_BLUE_NOISE_TEXTURE
    uint2 first = (pixel + uint2(offset * 128.0)) & 127u;
    uint2 second = (pixel + uint2(secondOffset * 128.0)) & 127u;
    uint row = (frameIndex & 63u) * 128u;
    return float4(VirtualShadowBlueNoise.Load(int3(first.x, first.y + row, 0)).gb,
        VirtualShadowBlueNoise.Load(int3(second.x, second.y + row, 0)).gb);
#else
    float2 seed = frac(float2(pixel) * float2(.06711056, .00583715) + float(frameIndex) * .61803398875);
    return frac(float4(seed + offset, seed.yx + secondOffset));
#endif
}

float2 VirtualShadowLocalDisk(float2 random)
{
    float2 square = random * sqrt(2.0) - sqrt(.5);
    float2 squared = square * square;
    float root = sqrt(2.0 * max(squared.x, squared.y) - min(squared.x, squared.y));
    if (squared.x > squared.y) square.x = square.x > 0.0 ? root : -root;
    else square.y = square.y > 0.0 ? root : -root;
    return square;
}

float VirtualShadowRayStepOffset(uint2 pixel, uint frameIndex)
{
#ifdef VSM_BLUE_NOISE_TEXTURE
    uint2 address = pixel & 127u;
    uint row = (frameIndex & 63u) * 128u;
    return VirtualShadowBlueNoise.Load(int3(address.x, address.y + row, 0)).r;
#else
    return VirtualShadowRayRandom(pixel, frameIndex, 0u, 1u).w;
#endif
}

void VirtualShadowRayBasis(float3 axis, out float3 tangent, out float3 bitangent)
{
    float signZ = axis.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (signZ + axis.z);
    float b = axis.x * axis.y * a;
    tangent = float3(1.0 + signZ * a * axis.x * axis.x, signZ * b, -signZ * axis.x);
    bitangent = float3(b, signZ + a * axis.y * axis.y, -axis.y);
}

VirtualShadowRay CreateVirtualShadowLocalRay(StructuredBuffer<VirtualShadowMapParameters> views,
    uint firstView, uint firstFace, uint lastFace, float3 start, float3 end,
    uint mip, float bias, float maxSlope, bool point)
{
    VirtualShadowRay ray = (VirtualShadowRay)0;
    ray.FirstView = firstView + firstFace;
    ray.SecondView = firstView + lastFace;
    ray.UseSecondFace = point && firstFace != lastFace;
    ray.MinMip = mip;
    ray.FirstStart = ProjectVirtualShadowRayPoint(views[ray.FirstView], start);
    ray.FirstStep = ProjectVirtualShadowRayPoint(views[ray.FirstView], end) - ray.FirstStart;
    if (!point) ray.FirstStart.xy = saturate(ray.FirstStart.xy);
    ray.FirstStart.z += bias;
    ray.FirstSlope = maxSlope * ray.FirstStep.z;
    if (ray.UseSecondFace)
    {
        ray.SecondStart = ProjectVirtualShadowRayPoint(views[ray.SecondView], start);
        ray.SecondStep = ProjectVirtualShadowRayPoint(views[ray.SecondView], end) - ray.SecondStart;
        ray.SecondStart.z += bias;
        ray.SecondSlope = maxSlope * ray.SecondStep.z;
    }
    return ray;
}

VirtualShadowProjectionResult TraceVirtualShadowLocal(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pages, Texture2DArray<uint> atlas,
    uint firstView, float3 position, float3 normal, float nominalPixelRadius, float dynamicBias,
    VirtualShadowRaySettings settings, uint2 pixel, uint frameIndex, float rayStartOffset)
{
    VirtualShadowProjectionResult result = EmptyVirtualShadowProjection();
    VirtualShadowMapParameters baseView = views[firstView];
    float3 toLight = baseView.LightPosition - position;
    float distance = length(toLight);
    float3 axis = toLight / distance;
    uint face = baseView.LightKind == VSM_LIGHT_POINT ? VirtualShadowPointFace(-toLight) : 0u;
    uint viewIndex = firstView + face;
    VirtualShadowMapParameters view = views[viewIndex];
    uint mip = VirtualShadowLocalSamplingMip(view, position, nominalPixelRadius, dynamicBias);
    if (settings.RayCount == 0u) return ProjectVirtualShadowHard(views, pages, atlas, viewIndex, position, normal, mip, rayStartOffset);
    float3 tangent, bitangent;
    VirtualShadowRayBasis(axis, tangent, bitangent);
    float coneSin = saturate(view.SourceRadius / distance);
    uint steps = coneSin == 0.0 ? 0u : settings.SamplesPerRay;
    float scale = max(.1 * view.WorldUnitsPerCentimeter, nominalPixelRadius * exp2(view.ResolutionLodBias));
    float ditherScale = settings.TexelDitherScale * view.TexelDitherScale * scale;
    float depthScale = abs(view.ProjectionDepthOffset) / (distance * distance);
    float maxBias = settings.MaxSlopeBias * scale * depthScale;
    float normalZ = dot(normal, axis);
    float2 tangentSlope = -float2(dot(normal, tangent), dot(normal, bitangent)) / normalZ;
    uint misses = 0u, rays = 0u;
    float hitDistances = 0.0;
    float stepOffset = VirtualShadowRayStepOffset(pixel, frameIndex);
    for (uint index = 0u; index < settings.RayCount; index++)
    {
        float4 random = VirtualShadowRayRandom(pixel, frameIndex, index, settings.RayCount);
        float2 disk = VirtualShadowLocalDisk(random.xy) * coneSin;
        float cosine = sqrt(max(1.0 - dot(disk, disk), 0.0));
        float3 direction = tangent * disk.x + bitangent * disk.y + axis * cosine;
        float2 offset = (random.zw - .5) * ditherScale;
        float offsetZ = min(settings.MaxSlopeBias * ditherScale, 2.0 * max(0.0, dot(tangentSlope, offset)));
        float3 origin = position + tangent * offset.x + bitangent * offset.y + axis * offsetZ;
        float sine = sqrt(max(1.0 - cosine * cosine, 0.0));
        float endDistance = distance * .75 * saturate(1.5 / (cosine + sine / tan(settings.MaxRayAngle)));
        float startDistance = min(max(rayStartOffset, 0.0), endDistance - 1e-6 * view.WorldUnitsPerCentimeter);
        float3 start = origin + direction * startDistance;
        float3 end = origin + direction * endDistance;
        float3 ndc;
        float2 uv;
        ProjectVirtualShadowPosition(view, start, ndc, uv);
        VirtualShadowDepthSample initial = ReadVirtualShadowProjectionDepth(views, pages, atlas, viewIndex, uv, mip);
        float bias = initial.Valid ? min(maxBias, VirtualShadowOptimalSlopeBias(views, viewIndex, initial, position, normal)) : 0.0;
        uint startFace = baseView.LightKind == VSM_LIGHT_POINT ? VirtualShadowPointFace(start - baseView.LightPosition) : 0u;
        uint endFace = baseView.LightKind == VSM_LIGHT_POINT ? VirtualShadowPointFace(end - baseView.LightPosition) : 0u;
        VirtualShadowRay ray = CreateVirtualShadowLocalRay(views, firstView, startFace, endFace,
            start, end, mip, bias, settings.ExtrapolateMaxSlope, baseView.LightKind == VSM_LIGHT_POINT);
        float depth;
        bool hit = TraceVirtualShadowRay(views, pages, atlas, ray, steps, stepOffset, depth);
        result.Valid = result.Valid || ray.AnyValid;
        result.ViewIndex = ray.SampledView;
        result.MipLevel = ray.SampledMip;
        rays++;
        if (!hit) misses++;
        else hitDistances += VirtualShadowOccluderDistance(views[ray.UseSecondFace ? ray.SecondView : ray.FirstView],
            depth, ray.UseSecondFace ? ray.SecondStart.z : ray.FirstStart.z, length(start - view.LightPosition));
        if (settings.AdaptiveRayCount > 0u && ((index == 0u && WaveActiveAllTrue(!hit)) ||
            (index >= settings.AdaptiveRayCount && WaveActiveAllTrue(misses == 0u)))) break;
        uint seed = asuint(stepOffset) * 0x915f77f5u + 0x93d765ddu;
        stepOffset = float(seed >> 8) * 5.96046447754e-08;
    }
    result.Visibility = float(misses) / float(rays);
    result.RayCount = rays;
    result.OccluderDistance = hitDistances / float(max(1u, rays - misses));
    return result;
}

VirtualShadowProjectionResult TraceVirtualShadowDirectionalFamily(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pages, Texture2DArray<uint> atlas,
    uint firstView, uint viewCount, float3 position, float3 normal, float dynamicBias, float receiverBias,
    VirtualShadowRaySettings settings, uint2 pixel, uint frameIndex, float distanceFromCamera, float rayStartOffset)
{
    if (viewCount == 0u) return EmptyVirtualShadowProjection();
    uint level = SelectDirectionalVirtualShadowView(views, firstView, viewCount, position, position,
        position, dynamicBias, receiverBias);
    if (level >= viewCount) return EmptyVirtualShadowProjection();
    uint requestedView = firstView + level;
    if (settings.RayCount == 0u) return ProjectVirtualShadowHard(views, pages, atlas, requestedView, position, normal, 0u, rayStartOffset);
    float3 ndc;
    float2 uv;
    ProjectVirtualShadowPosition(views[requestedView], position, ndc, uv);
    VirtualShadowDepthSample initial = ReadVirtualShadowProjectionDepth(views, pages, atlas, requestedView, uv, 0u);
    uint viewIndex = initial.Valid ? initial.ViewIndex : requestedView;
    VirtualShadowMapParameters view = views[viewIndex];
    float3 axis = -view.LightDirection;
    ProjectVirtualShadowPosition(view, position, ndc, uv);
    float3 tangent, bitangent;
    tangent = cross(axis, abs(axis.x) > 1e-6 ? float3(1,0,0) : float3(0,1,0));
    bitangent = cross(tangent, axis);
    float2 slope = clamp(-CalculateVirtualShadowReceiverPlaneDepthGradient(view, position, normal), -.05.xx, .05.xx);
    float ditherScale = .5 / float(view.VirtualResolution) * settings.TexelDitherScale * view.TexelDitherScale *
        (distanceFromCamera / view.WorldUnitsPerCentimeter) / exp2(float(view.ClipmapLevel) - view.ResolutionLodBias);
    float rayLength = settings.RayLengthScale * distanceFromCamera;
    VirtualShadowProjectionResult result = EmptyVirtualShadowProjection();
    uint misses = 0u, rays = 0u;
    float hitDistances = 0.0;
    float stepOffset = VirtualShadowRayStepOffset(pixel, frameIndex);
    for (uint index = 0u; index < settings.RayCount; index++)
    {
        float4 random = VirtualShadowRayRandom(pixel, frameIndex, index, settings.RayCount);
        float2 disk = VirtualShadowConcentricDisk(random.xy) * sin(.5 * view.SourceAngleRadians);
        float3 direction = normalize(axis + tangent * disk.x + bitangent * disk.y);
        float3 start = position + direction * max(rayStartOffset, 0.0);
        VirtualShadowRay ray = (VirtualShadowRay)0;
        ray.FirstView = viewIndex;
        ray.FirstStart = ProjectVirtualShadowRayPoint(view, start);
        float4 step = mul(float4(direction * rayLength, 0.0), view.LightViewProjection);
        ray.FirstStep = float3(step.xy * float2(.5,-.5), -step.z);
        float2 offset = (random.zw - .5) * ditherScale;
        ray.FirstStart.xy += offset;
        ray.FirstStart.z += max(0.0, 2.0 * max(0.0, dot(slope, offset)) - abs(rayStartOffset * view.ProjectionDepthScale));
        ray.FirstSlope = abs(settings.ExtrapolateMaxSlope * view.WorldUnitsPerCentimeter * view.ProjectionDepthScale);
        float depth;
        bool hit = TraceVirtualShadowRay(views, pages, atlas, ray, settings.SamplesPerRay, stepOffset, depth);
        result.Valid = result.Valid || ray.AnyValid;
        result.ViewIndex = ray.SampledView;
        rays++;
        if (!hit) misses++;
        else hitDistances += VirtualShadowOccluderDistance(view, depth, ray.FirstStart.z, 0.0);
        if (settings.AdaptiveRayCount > 0u && ((index == 0u && WaveActiveAllTrue(!hit)) ||
            (index >= settings.AdaptiveRayCount && WaveActiveAllTrue(misses == 0u)))) break;
    }
    result.Visibility = float(misses) / float(rays);
    result.RayCount = rays;
    result.OccluderDistance = hitDistances / float(max(1u, rays - misses));
    return result;
}


VirtualShadowProjectionResult TraceVirtualShadowDirectional(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pages, Texture2DArray<uint> atlas,
    uint firstView, uint viewCount, float3 position, float3 normal, float dynamicBias, float receiverBias,
    VirtualShadowRaySettings settings, uint2 pixel, uint frameIndex, float distanceFromCamera, float rayStartOffset,
    bool firstPersonReceiver = false, uint firstPersonView = 0xFFFFFFFFu)
{
    VirtualShadowProjectionResult result = TraceVirtualShadowDirectionalFamily(views, pages, atlas,
        firstView, viewCount, position, normal, dynamicBias, receiverBias, settings, pixel, frameIndex,
        distanceFromCamera, rayStartOffset);
    if (!firstPersonReceiver && firstPersonView != 0xFFFFFFFFu && !(result.Valid && result.Visibility == 0.0))
    {
        VirtualShadowProjectionResult firstPerson = TraceVirtualShadowDirectionalFamily(views, pages, atlas,
            firstPersonView, views[firstPersonView].ClipmapLevelCount, position, normal, dynamicBias, receiverBias,
            settings, pixel, frameIndex, distanceFromCamera, rayStartOffset);
        if (firstPerson.Valid && (!result.Valid || firstPerson.Visibility <= result.Visibility)) result = firstPerson;
    }
    return result;
}

#endif
