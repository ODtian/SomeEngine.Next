#ifndef VSM_SOFT_SAMPLING_SLANG
#define VSM_SOFT_SAMPLING_SLANG

#include "vsm_projection.slang"

static const uint VSM_SOFT_MAX_BLOCKER_QUERIES = 4u;
static const uint VSM_SOFT_MAX_FILTER_QUERIES = 8u;

struct VirtualShadowSoftPlane
{
    uint ViewIndex;
    float3 Ndc;
    float2 UV;
    float2 Gradient;
};

VirtualShadowSoftPlane BuildVirtualShadowSoftPlane(StructuredBuffer<VirtualShadowMapParameters> views,
    uint viewIndex, float3 position, float3 normal)
{
    VirtualShadowSoftPlane plane;
    plane.ViewIndex = viewIndex;
    VirtualShadowMapParameters view = views[viewIndex];
    ProjectVirtualShadowPosition(view, position, plane.Ndc, plane.UV);
    plane.Gradient = CalculateVirtualShadowReceiverPlaneDepthGradient(view, plane.Ndc, normal);
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

bool ReadVirtualShadowSoftDepth(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas,
    uint firstView, uint requestedView, uint viewCount, uint mip, float3 position,
    float2 sampleUV, float3 normal, VirtualShadowSoftPlane centerPlane,
    out VirtualShadowDepthSample sample, out bool blocked, out float separation)
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
    if (IsDirectionalVirtualShadowProjection(baseView) && sample.ViewIndex != queryView)
    {
        float4 transform = VirtualShadowClipmapScaleBias(views[queryView], source, sample.ViewIndex - queryView);
        stored = stored * transform.x + 1.0 - transform.x - transform.w;
    }
    float receiver = 1.0 - (plane.Ndc.z + dot(sample.TexelCenter - plane.UV, plane.Gradient));
    blocked = stored > receiver;
    if (blocked)
        separation = VirtualShadowOccluderDistance(source, stored, receiver, length(position - source.LightPosition));
    return true;
}

VirtualShadowProjectionResult SampleVirtualShadowSoftView(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, uint firstView, uint requestedView,
    uint viewCount, float3 position, float3 normal, uint mip, uint blockerSampleCount,
    uint filterSampleCount, float maxRadiusTexels, uint2 pixel, uint frameIndex)
{
    VirtualShadowMapParameters view = views[requestedView];
    float emitter = IsDirectionalVirtualShadowProjection(view) ? tan(.5 * view.SourceAngleRadians) : view.SourceRadius;
    if (emitter <= 0.0 || maxRadiusTexels <= 0.0)
        return ProjectVirtualShadowHard(views, pages, atlas, requestedView, position, normal, mip, 0.0);
    VirtualShadowProjectionResult result = EmptyVirtualShadowProjection();
    VirtualShadowSoftPlane plane = BuildVirtualShadowSoftPlane(views, requestedView, position, normal);
    VirtualShadowDepthSample center;
    bool centerBlocked;
    float centerSeparation;
    if (!ReadVirtualShadowSoftDepth(views, pages, atlas, firstView, requestedView, viewCount, mip,
            position, plane.UV, normal, plane, center, centerBlocked, centerSeparation)) return result;
    if (center.ViewIndex != plane.ViewIndex)
        plane = BuildVirtualShadowSoftPlane(views, center.ViewIndex, position, normal);
    result.Valid = true;
    result.ViewIndex = center.ViewIndex;
    result.MipLevel = center.MipLevel;
    VirtualShadowMapParameters source = views[center.ViewIndex];
    float4 clip = mul(float4(position, 1.0), source.LightViewProjection);
    float projectionScale = .5 * length(float3(source.LightViewProjection[0][0],
        source.LightViewProjection[1][0], source.LightViewProjection[2][0])) / max(abs(clip.w), 1e-20);
    float texelsPerWorldUnit = projectionScale * float(VirtualShadowMipResolution(source, center.MipLevel));
    float maxRadiusWorld = maxRadiusTexels / max(texelsPerWorldUnit, 1e-20);
    uint resolution = VirtualShadowMipResolution(source, center.MipLevel);
    requestedView = center.ViewIndex;
    mip = center.MipLevel;
    float minimumDistance = 1e-6 * view.WorldUnitsPerCentimeter;
    float distanceToLight = length(position - view.LightPosition);
    float maximumSeparation = IsDirectionalVirtualShadowProjection(view)
        ? max(plane.Ndc.z, 0.0) / max(abs(source.ProjectionDepthScale), 1e-20)
        : max(distanceToLight - minimumDistance, 0.0);
    float searchRadius = IsDirectionalVirtualShadowProjection(view)
        ? emitter * maximumSeparation : emitter * maximumSeparation / minimumDistance;
    searchRadius = min(searchRadius, maxRadiusWorld);
    float angle = VirtualShadowRayStepOffset(pixel, frameIndex) * 6.28318530718;
    float sine, cosine;
    sincos(angle, sine, cosine);
    uint blockerCount = centerBlocked ? 1u : 0u;
    float separationSum = centerBlocked ? centerSeparation : 0.0;
    uint searchCount = clamp(blockerSampleCount, 1u, VSM_SOFT_MAX_BLOCKER_QUERIES);
    for (uint index = 1u; index < searchCount; index++)
    {
        float2 triangle = index == 1u ? float2(1.0, 0.0) :
            index == 2u ? float2(-.5, .8660254038) : float2(-.5, -.8660254038);
        float2 disk = RotateVirtualShadowDiskSample(triangle, sine, cosine);
        float2 queryUV = plane.UV + disk * (searchRadius * texelsPerWorldUnit / float(resolution));
        VirtualShadowDepthSample sample;
        bool blocked;
        float separation;
        if (!ReadVirtualShadowSoftDepth(views, pages, atlas, firstView, requestedView, viewCount, mip,
                position, queryUV, normal, plane, sample, blocked, separation))
        {
            result.Valid = false;
            return result;
        }
        if (blocked) { blockerCount++; separationSum += separation; }
    }
    if (blockerCount == 0u) { result.Visibility = 1.0; return result; }
    float averageSeparation = separationSum / float(blockerCount);
    float radius = IsDirectionalVirtualShadowProjection(view) ? emitter * averageSeparation :
        emitter * averageSeparation / max(distanceToLight - averageSeparation, minimumDistance);
    radius = min(radius, maxRadiusWorld);
    float visible = 0.0;
    uint samples = clamp(filterSampleCount, 1u, VSM_SOFT_MAX_FILTER_QUERIES);
    for (uint index = 0u; index < samples; index++)
    {
        float2 disk = RotateVirtualShadowDiskSample(VirtualShadowDiskSample(index), sine, cosine);
        float2 queryUV = plane.UV + disk * (radius * texelsPerWorldUnit / float(resolution));
        VirtualShadowDepthSample sample;
        bool blocked;
        float separation;
        if (!ReadVirtualShadowSoftDepth(views, pages, atlas, firstView, requestedView, viewCount, mip,
                position, queryUV, normal, plane, sample, blocked, separation))
        {
            result.Valid = false;
            return result;
        }
        visible += blocked ? 0.0 : 1.0;
    }
    result.Visibility = visible / float(samples);
    result.OccluderDistance = averageSeparation;
    return result;
}

VirtualShadowProjectionResult SampleVirtualShadowDirectionalSoft(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, uint firstView, uint viewCount,
    float3 position, float3 normal, float dynamicBias, float receiverBias, uint blockerSamples,
    uint filterSamples, float maxRadiusTexels, uint2 pixel, uint frameIndex,
    bool firstPersonReceiver = false, uint firstPersonView = 0xffffffffu)
{
    if (viewCount == 0u) return EmptyVirtualShadowProjection();
    uint level = SelectDirectionalVirtualShadowView(views, firstView, viewCount, position, position,
        position, dynamicBias, receiverBias);
    if (level >= viewCount) return EmptyVirtualShadowProjection();
    VirtualShadowProjectionResult result = SampleVirtualShadowSoftView(views, pages, atlas,
        firstView, firstView + level, viewCount, position, normal, 0u, blockerSamples,
        filterSamples, maxRadiusTexels, pixel, frameIndex);
    if (!firstPersonReceiver && firstPersonView != 0xffffffffu && !(result.Valid && result.Visibility == 0.0))
    {
        uint count = views[firstPersonView].ClipmapLevelCount;
        uint selected = SelectDirectionalVirtualShadowView(views, firstPersonView, count,
            position, position, position, dynamicBias, receiverBias);
        if (selected < count)
        {
            VirtualShadowProjectionResult firstPerson = SampleVirtualShadowSoftView(views, pages, atlas,
                firstPersonView, firstPersonView + selected, count, position, normal, 0u,
                blockerSamples, filterSamples, maxRadiusTexels, pixel, frameIndex);
            if (firstPerson.Valid && (!result.Valid || firstPerson.Visibility <= result.Visibility)) result = firstPerson;
        }
    }
    return result;
}

VirtualShadowProjectionResult SampleVirtualShadowLocalSoft(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pages, Texture2DArray<uint> atlas, uint firstView, float3 position,
    float3 normal, float nominalPixelRadius, float dynamicBias, uint blockerSamples,
    uint filterSamples, float maxRadiusTexels, uint2 pixel, uint frameIndex)
{
    VirtualShadowMapParameters view = views[firstView];
    uint face = view.LightKind == VSM_LIGHT_POINT ? VirtualShadowPointFace(position - view.LightPosition) : 0u;
    uint requested = firstView + face;
    uint mip = VirtualShadowLocalSamplingMip(views[requested], position, nominalPixelRadius, dynamicBias);
    return SampleVirtualShadowSoftView(views, pages, atlas, firstView, requested,
        view.LightKind == VSM_LIGHT_POINT ? 6u : 1u, position, normal, mip,
        blockerSamples, filterSamples, maxRadiusTexels, pixel, frameIndex);
}

#endif
