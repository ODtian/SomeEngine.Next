#ifndef VSM_SOFT_SAMPLING_SLANG
#define VSM_SOFT_SAMPLING_SLANG

#include "vsm_projection.slang"
#include "vsm_prefilter_sampling.slang"

static const uint VSM_SOFT_MAX_BLOCKER_QUERIES = 4u;
static const uint VSM_SOFT_MAX_FILTER_QUERIES = 8u;

struct VirtualShadowSoftPlane
{
    uint ViewIndex;
    float3 Ndc;
    float2 UV;
    float2 Gradient;
    float DepthOffset;
};

VirtualShadowSoftPlane BuildVirtualShadowSoftPlane(StructuredBuffer<VirtualShadowMapParameters> views,
    uint viewIndex, float3 position, float3 normal)
{
    VirtualShadowSoftPlane plane;
    plane.ViewIndex = viewIndex;
    VirtualShadowMapParameters view = views[viewIndex];
    ProjectVirtualShadowPosition(view, position, plane.Ndc, plane.UV);
    plane.Gradient = CalculateVirtualShadowReceiverPlaneDepthGradient(view, position, normal);
    plane.DepthOffset = 0.0;
    if (IsDirectionalVirtualShadowProjection(view))
        plane.DepthOffset = CalculateVirtualShadowDirectionalDepthOffset(view, position);
    return plane;
}

bool ProjectVirtualShadowSoftCubeTap(StructuredBuffer<VirtualShadowMapParameters> views,
    uint firstView, uint viewIndex, float2 uv, float3 position, float3 normal,
    out uint queryView, out float2 queryUV)
{
    VirtualShadowMapParameters view = views[viewIndex];
    float3 right = normalize(float3(view.LightViewProjection[0][0], view.LightViewProjection[1][0], view.LightViewProjection[2][0]));
    float3 up = normalize(float3(view.LightViewProjection[0][1], view.LightViewProjection[1][1], view.LightViewProjection[2][1]));
    float3 forward = normalize(float3(view.LightViewProjection[0][3], view.LightViewProjection[1][3], view.LightViewProjection[2][3]));
    float3 ray = normalize(forward + right * (uv.x * 2.0 - 1.0) + up * (1.0 - uv.y * 2.0));
    float facing = dot(normal, ray);
    queryView = viewIndex;
    queryUV = uv;
    if (abs(facing) < 1e-8) return false;
    float distance = dot(normal, position - view.LightPosition) / facing;
    if (distance <= 0.0) return false;
    float3 queryPosition = view.LightPosition + ray * distance;
    queryView = firstView + VirtualShadowPointFace(queryPosition - view.LightPosition);
    float3 ndc;
    return ProjectVirtualShadowPosition(views[queryView], queryPosition, ndc, queryUV);
}

float2 ProjectVirtualShadowLocalSourceTap(VirtualShadowMapParameters view, float3 position,
    float separation, float2 disk)
{
    float3 lightToReceiver = position - view.LightPosition;
    float distance = length(lightToReceiver);
    float3 direction = lightToReceiver / max(distance, 1e-20);
    float3 axis = abs(direction.z) < 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    float3 right = normalize(cross(direction, axis));
    float3 up = cross(direction, right);
    float sourceRatio = min(view.SourceRadius / max(distance, 1e-20), 1.0);
    float coneArea = sourceRatio * sourceRatio /
        (1.0 + sqrt(max(1.0 - sourceRatio * sourceRatio, 0.0)));
    float radialSquared = dot(disk, disk);
    float angularArea = radialSquared * coneArea;
    float tangent = sqrt(max(angularArea * (2.0 - angularArea), 0.0)) /
        max(1.0 - angularArea, 1e-20);
    float3 offset = (right * disk.x + up * disk.y) *
        (separation * tangent / max(sqrt(radialSquared), 1e-20));
    float3 blocker = position - direction * separation + offset;
    float4 clip = ProjectVirtualShadowClip(view, blocker);
    return clip.xy / clip.w * float2(0.5, -0.5) + 0.5;
}

float2 CalculateVirtualShadowSolarDiskOffset(float2 disk, float sourceArea, float sourceTangent,
    float radiusWorld)
{
    // Disk strata describe solid angle. Project each stratum onto the blocker
    // plane using tan(angle), rather than treating it as a flat emitter disk.
    float radialSquared = dot(disk, disk);
    float angularArea = radialSquared * sourceArea;
    float tangent = sqrt(max(angularArea * (2.0 - angularArea), 0.0)) /
        max(1.0 - angularArea, 1e-20);
    return disk * (radiusWorld * tangent / max(sourceTangent * sqrt(radialSquared), 1e-20));
}

bool ReadVirtualShadowSoftDepth(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas,
    uint firstView, uint requestedView, uint viewCount, uint mip, float3 position,
    float2 sampleUV, float3 normal, VirtualShadowSoftPlane centerPlane,
    out VirtualShadowDepthSample sample, out bool blocked, out float separation, float rayStartDistance)
{
    VirtualShadowMapParameters baseView = views[firstView];
    uint queryView = requestedView;
    float2 uv = sampleUV;
    sample = (VirtualShadowDepthSample)0;
    blocked = false;
    separation = 0.0;
    if (IsDirectionalVirtualShadowProjection(baseView))
    {
        while (queryView < firstView + viewCount && (any(uv < 0.0) || any(uv >= 1.0)))
        {
            queryView++;
            if (queryView < firstView + viewCount)
            {
                float4 transform = VirtualShadowClipmapScaleBias(views[requestedView], views[queryView], queryView - requestedView);
                uv = sampleUV * transform.x + transform.yz;
            }
        }
        if (queryView == firstView + viewCount) return false;
    }
    else if (any(uv < 0.0) || any(uv >= 1.0))
    {
        if (baseView.LightKind != VSM_LIGHT_POINT ||
            !ProjectVirtualShadowSoftCubeTap(views, firstView, requestedView, sampleUV, position, normal, queryView, uv)) return false;
    }
    sample = ReadVirtualShadowProjectionDepth(views, pages, atlas, queryView, uv, mip);
    if (!sample.Valid) return false;
    VirtualShadowMapParameters source = views[sample.ViewIndex];
    VirtualShadowSoftPlane plane = centerPlane;
    if (plane.ViewIndex != sample.ViewIndex)
        plane = BuildVirtualShadowSoftPlane(views, sample.ViewIndex, position, normal);
    float stored = sample.Depth;
    precise float receiver = 1.0 - plane.Ndc.z;
    float planeOffset = dot(sample.UV - plane.UV, plane.Gradient);
    precise float centerDepthDifference = IsDirectionalVirtualShadowProjection(baseView)
        ? (stored - 0.5) + plane.DepthOffset : stored - receiver;
    precise float depthDifference = centerDepthDifference + planeOffset;
    float slopeBias = CalculateVirtualShadowSlopeBias(source, sample, plane.Gradient);
    float roundingRadius = VirtualShadowReverseDepthRoundingRadius(stored)
        + VirtualShadowReverseDepthRoundingRadius(receiver);
    float rayStartBias = CalculateVirtualShadowRayStartBias(source, position, rayStartDistance);
    blocked = depthDifference - slopeBias - rayStartBias > roundingRadius;
    if (blocked)
        separation = IsDirectionalVirtualShadowProjection(baseView)
            ? max(1e-6 * source.WorldUnitsPerCentimeter, centerDepthDifference / abs(source.ProjectionDepthScale))
            : VirtualShadowOccluderDistance(source, stored, receiver, length(position - source.LightPosition));
    return true;
}

VirtualShadowProjectionResult SampleVirtualShadowSoftView(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, uint firstView, uint requestedView,
    uint viewCount, float3 position, float3 normal, uint mip, uint blockerSampleCount,
    uint filterSampleCount, float maxRadiusTexels, uint2 pixel, uint frameIndex, float rayStartDistance = 0.0)
{
    VirtualShadowMapParameters view = views[requestedView];
    float emitter = IsDirectionalVirtualShadowProjection(view) ? tan(.5 * view.SourceAngleRadians) : view.SourceRadius;
    if (emitter <= 0.0 || maxRadiusTexels <= 0.0)
        return ProjectVirtualShadowHard(views, pages, atlas, requestedView, position, normal, mip, rayStartDistance);
    VirtualShadowProjectionResult result = EmptyVirtualShadowProjection();
    VirtualShadowSoftPlane plane = BuildVirtualShadowSoftPlane(views, requestedView, position, normal);
    VirtualShadowDepthSample center;
    bool centerBlocked;
    float centerSeparation;
    bool centerValid = ReadVirtualShadowSoftDepth(views, pages, atlas, firstView, requestedView, viewCount, mip,
        position, plane.UV, normal, plane, center, centerBlocked, centerSeparation, rayStartDistance);
    if (centerValid && center.ViewIndex != plane.ViewIndex)
        plane = BuildVirtualShadowSoftPlane(views, center.ViewIndex, position, normal);
    if (!centerValid) { center.ViewIndex = requestedView; center.MipLevel = mip; }
    result.ViewIndex = center.ViewIndex;
    result.MipLevel = center.MipLevel;
    VirtualShadowMapParameters source = views[center.ViewIndex];
    float4 clip = ProjectVirtualShadowClip(source, position);
    float projectionScale = .5 * length(float3(source.LightViewProjection[0][0],
        source.LightViewProjection[1][0], source.LightViewProjection[2][0])) / max(abs(clip.w), 1e-20);
    float texelsPerWorldUnit = projectionScale * float(VirtualShadowMipResolution(source, center.MipLevel));
    float4 uvClip = mul(float4(position + source.WorldTranslation, 1.0), source.ShadowUv);
    float2 uvPerWorldUnit = float2(
        length(float3(source.ShadowUv[0][0], source.ShadowUv[1][0], source.ShadowUv[2][0])),
        length(float3(source.ShadowUv[0][1], source.ShadowUv[1][1], source.ShadowUv[2][1]))) /
        max(abs(uvClip.w), 1e-20);
    float sourceCosine = rsqrt(1.0 + emitter * emitter);
    float sourceArea = emitter * emitter * sourceCosine * sourceCosine / (1.0 + sourceCosine);
    float footprintTexelsPerWorldUnit = texelsPerWorldUnit;
    if (center.ViewIndex != requestedView || center.MipLevel != mip)
    {
        float4 requestedClip = ProjectVirtualShadowClip(view, position);
        float requestedScale = .5 * length(float3(view.LightViewProjection[0][0],
            view.LightViewProjection[1][0], view.LightViewProjection[2][0])) / max(abs(requestedClip.w), 1e-20);
        footprintTexelsPerWorldUnit = max(footprintTexelsPerWorldUnit,
            requestedScale * float(VirtualShadowMipResolution(view, mip)));
    }
    float maxRadiusWorld = maxRadiusTexels / max(footprintTexelsPerWorldUnit, 1e-20);
    requestedView = center.ViewIndex;
    mip = center.MipLevel;
    float minimumDistance = 1e-6 * view.WorldUnitsPerCentimeter;
    float distanceToLight = length(position - view.LightPosition);
    float maximumSeparation = IsDirectionalVirtualShadowProjection(view)
        ? max(plane.Ndc.z, 0.0) / max(abs(source.ProjectionDepthScale), 1e-20)
        : max(distanceToLight - minimumDistance, 0.0);
    float searchRadius = IsDirectionalVirtualShadowProjection(view)
        ? emitter * maximumSeparation : CalculateVirtualShadowLocalSourceSupport(view, position);
    searchRadius = min(searchRadius, maxRadiusWorld);
    float angle = VirtualShadowRayStepOffset(pixel, frameIndex) * 6.28318530718;
    float sine, cosine;
    sincos(angle, sine, cosine);
    uint blockerCount = centerBlocked ? 1u : 0u;
    uint validSearches = centerValid ? 1u : 0u;
    float separationSum = centerBlocked ? centerSeparation : 0.0;
    uint searchCount = clamp(blockerSampleCount, 1u, VSM_SOFT_MAX_BLOCKER_QUERIES);
    for (uint index = 1u; index < searchCount; index++)
    {
        float2 triangle = index == 1u ? float2(1.0, 0.0) :
            index == 2u ? float2(-.5, .8660254038) : float2(-.5, -.8660254038);
        float2 disk = RotateVirtualShadowDiskSample(triangle, sine, cosine);
        float searchSeparation = distanceToLight * searchRadius / max(emitter + searchRadius, minimumDistance);
        float2 queryUV = IsDirectionalVirtualShadowProjection(view)
            ? plane.UV + CalculateVirtualShadowSolarDiskOffset(disk, sourceArea, emitter, searchRadius) * uvPerWorldUnit
            : ProjectVirtualShadowLocalSourceTap(source, position, searchSeparation, disk);
        VirtualShadowDepthSample sample;
        bool blocked;
        float separation;
        if (!ReadVirtualShadowSoftDepth(views, pages, atlas, firstView, requestedView, viewCount, mip,
                position, queryUV, normal, plane, sample, blocked, separation, rayStartDistance))
            continue;
        validSearches++;
        // A directional emitter can reach this query only inside the cone at
        // the actual blocker depth. A wider search can also find nearby objects
        // outside that cone; those objects do not determine this penumbra.
        bool insideSource = !IsDirectionalVirtualShadowProjection(view) ||
            length(disk) * searchRadius <= emitter * separation;
        if (blocked && insideSource) { blockerCount++; separationSum += separation; }
    }
    if (validSearches == 0u) return result;
    if (blockerCount == 0u) { result.Valid = true; result.Visibility = 1.0; return result; }
    float averageSeparation = separationSum / float(blockerCount);
    float radius = IsDirectionalVirtualShadowProjection(view) ? emitter * averageSeparation :
        emitter * averageSeparation / max(distanceToLight - averageSeparation, minimumDistance);
    if (IsDirectionalVirtualShadowProjection(view)) radius = min(radius, maxRadiusWorld);
    float visible = 0.0;
    uint validFilters = 0u;
    uint samples = clamp(filterSampleCount, 1u, VSM_SOFT_MAX_FILTER_QUERIES);
    for (uint index = 0u; index < samples; index++)
    {
        float2 disk = RotateVirtualShadowDiskSample(VirtualShadowDiskSample(index, samples), sine, cosine);
        float2 queryUV = IsDirectionalVirtualShadowProjection(view)
            ? plane.UV + CalculateVirtualShadowSolarDiskOffset(disk, sourceArea, emitter, radius) * uvPerWorldUnit
            : ProjectVirtualShadowLocalSourceTap(source, position, averageSeparation, disk);
        VirtualShadowDepthSample sample;
        bool blocked;
        float separation;
        if (!ReadVirtualShadowSoftDepth(views, pages, atlas, firstView, requestedView, viewCount, mip,
                position, queryUV, normal, plane, sample, blocked, separation, rayStartDistance))
            continue;
        validFilters++;
        visible += blocked ? 0.0 : 1.0;
    }
    result.Valid = validFilters != 0u;
    if (result.Valid) result.Visibility = visible / float(validFilters);
    result.OccluderDistance = averageSeparation;
    return result;
}

VirtualShadowProjectionResult SampleVirtualShadowDirectionalSoft(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, uint firstView, uint viewCount,
    float3 position, float3 normal, float dynamicBias, float receiverBias, uint blockerSamples,
    uint filterSamples, float maxRadiusTexels, uint2 pixel, uint frameIndex,
    bool firstPersonReceiver = false, uint firstPersonView = 0xffffffffu, float rayStartDistance = 0.0)
{
    if (viewCount == 0u) return EmptyVirtualShadowProjection();
    uint level = SelectDirectionalVirtualShadowView(views, firstView, viewCount, position, position,
        position, dynamicBias, receiverBias);
    if (level >= viewCount) return EmptyVirtualShadowProjection();
    VirtualShadowProjectionResult result = SampleVirtualShadowSoftView(views, pages, atlas,
        firstView, firstView + level, viewCount, position, normal, 0u, blockerSamples,
        filterSamples, maxRadiusTexels, pixel, frameIndex, rayStartDistance);
    if (!firstPersonReceiver && firstPersonView != 0xffffffffu && !(result.Valid && result.Visibility == 0.0))
    {
        uint count = views[firstPersonView].ClipmapLevelCount;
        uint selected = SelectDirectionalVirtualShadowView(views, firstPersonView, count,
            position, position, position, dynamicBias, receiverBias);
        if (selected < count)
        {
            VirtualShadowProjectionResult firstPerson = SampleVirtualShadowSoftView(views, pages, atlas,
                firstPersonView, firstPersonView + selected, count, position, normal, 0u,
                blockerSamples, filterSamples, maxRadiusTexels, pixel, frameIndex, rayStartDistance);
            if (firstPerson.Valid && (!result.Valid || firstPerson.Visibility <= result.Visibility)) result = firstPerson;
        }
    }
    return result;
}

VirtualShadowProjectionResult SampleVirtualShadowLocalSoft(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, uint firstView, float3 position,
    float3 normal, float nominalPixelRadius, float dynamicBias, uint blockerSamples,
    uint filterSamples, float maxRadiusTexels, uint2 pixel, uint frameIndex, float rayStartDistance = 0.0)
{
    VirtualShadowMapParameters view = views[firstView];
    uint face = view.LightKind == VSM_LIGHT_POINT ? VirtualShadowPointFace(position - view.LightPosition) : 0u;
    uint requested = firstView + face;
    uint mip = VirtualShadowLocalSamplingMip(views[requested], position, nominalPixelRadius, dynamicBias);
    return SampleVirtualShadowSoftView(views, pages, atlas, firstView, requested,
        view.LightKind == VSM_LIGHT_POINT ? 6u : 1u, position, normal, mip,
        blockerSamples, filterSamples, maxRadiusTexels, pixel, frameIndex, rayStartDistance);
}

VirtualShadowProjectionResult SampleVirtualShadowDirectionalFilteredSoft(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pages,
    Texture2DArray<uint> atlas, Texture2D<float> filteredAtlas, uint firstView, uint viewCount,
    float3 position, float3 normal, float receiverBias, uint blockerSamples, uint filterSamples,
    float maxRadiusTexels, uint2 pixel, uint frameIndex,
    bool firstPersonReceiver = false, uint firstPersonView = 0xffffffffu, float rayStartDistance = 0.0)
{
    VirtualShadowPrefilterResult distant = SampleDirectionalVirtualShadowPrefiltered(
        views, pages, filteredAtlas, firstView, viewCount, position);
    VirtualShadowProjectionResult filtered = EmptyVirtualShadowProjection();
    filtered.Visibility = distant.Visibility;
    filtered.Valid = distant.Valid;
    filtered.ViewIndex = distant.ViewIndex;
    filtered.OccluderDistance = distant.OccluderDistance;
    filtered.MipLevel = 0u;
    if (filtered.Valid && filtered.Visibility <= 0.0001) return filtered;
    // Directional sampling follows the receiver/per-light choice. Pool pressure
    // changes requested residency; the existing mapped-coarse lookup supplies it.
    VirtualShadowProjectionResult soft = SampleVirtualShadowDirectionalSoft(views, pages, atlas,
        firstView, viewCount, position, normal, 0.0, receiverBias, blockerSamples, filterSamples,
        maxRadiusTexels, pixel, frameIndex, firstPersonReceiver, firstPersonView, rayStartDistance);
    if (filtered.Valid)
    {
        if (soft.Visibility < 1.0)
            filtered.OccluderDistance = lerp(filtered.OccluderDistance, soft.OccluderDistance, filtered.Visibility);
        filtered.Visibility *= soft.Visibility;
        return filtered;
    }
    return soft;
}

#endif
