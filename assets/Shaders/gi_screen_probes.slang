#include "cluster_view.slang"
#include "visbuffer_encoding.slang"
#include "gi_normal.slang"
#include "gi_probe_data.slang"

ConstantBuffer<GiScreenSettings> GiSettings;
StructuredBuffer<ClusterViewUniforms> GiViews;
Texture2DArray<float> GiDepth;
Texture2DArray<uint> GiVisibility;
Texture2DArray<float2> GiMotion;
Texture2DArray<float4> GiNormal;
Texture2DArray<float4> GiDiffuse;
Texture2DArray<float4> GiReflectedRadiance;
RWStructuredBuffer<GiProbeSurface> GiProbeSurfaces;
RWStructuredBuffer<float4> GiProbeRadiance;
static const float GI_PI = 3.14159265359;

uint GiProbeCount() { return GiSettings.ProbeWidth * GiSettings.ProbeHeight * GiSettings.ViewCount; }

uint GiProbeIndex(uint2 pixel, uint view)
{
    uint2 probe = min(pixel / GI_PROBE_SPACING, uint2(GiSettings.ProbeWidth, GiSettings.ProbeHeight) - 1u);
    return (view * GiSettings.ProbeHeight + probe.y) * GiSettings.ProbeWidth + probe.x;
}
uint3 GiProbePixel(uint index)
{
    uint view = index / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    uint local = index % (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    uint2 pixel = uint2(local % GiSettings.ProbeWidth, local / GiSettings.ProbeWidth) * GI_PROBE_SPACING + GI_PROBE_SPACING / 2u;
    return uint3(min(pixel, uint2(GiSettings.Width, GiSettings.Height) - 1u), view);
}

float3 GiWorldPosition(uint3 pixel)
{
    float2 uv = (float2(pixel.xy) + 0.5) / float2(GiSettings.Width, GiSettings.Height);
    float4 world = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0,
        GiDepth.Load(int4(pixel, 0)), 1.0), GiViews[pixel.z].ClipToWorld);
    return world.xyz / world.w;
}

float GiPixelFootprint(float3 position, uint view)
{
    float3 viewPosition = ClusterPositionForView(position, GiViews[view]);
    return max(0.001, abs(mul(float4(viewPosition, 1.0), GiViews[view].View).z) *
        2.0 / (abs(GiViews[view].P11) * float(GiSettings.Height)));
}

float3 GiPlaneNormal(uint3 pixel)
{
    return DecodeGiNormal(GiNormal.Load(int4(pixel, 0)).zw);
}

float GiRandom(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return float(value >> 8u) * (1.0 / 16777216.0);
}

// The screen stage uses the renderer's depth and visible surfaces. Its misses
// remain available to the Mesh SDF and Global SDF stages during their migration.
bool TraceGiScreen(float3 origin, float3 direction, uint view, out uint3 hitPixel)
{
    hitPixel = 0u;
    float3 originForView = ClusterPositionForView(origin, GiViews[view]);
    float previousDistance = 0.0;
    for (uint step = 1u; step <= 20u; step++)
    {
        float distance = 2.0 * float(step * step) / 400.0;
        float4 clip = mul(float4(originForView + direction * distance, 1.0), GiViews[view].ViewProj);
        if (clip.w <= 0.0) return false;
        float2 uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
        if (any(uv < 0.0) || any(uv >= 1.0)) return false;
        uint3 pixel = uint3(uint2(uv * float2(GiSettings.Width, GiSettings.Height)), view);
        uint triangle = GiVisibility.Load(int4(pixel, 0));
        if (!IsVisBufferEmpty(triangle) && GiDiffuse.Load(int4(pixel, 0)).w > 0.0)
        {
            float3 surface = GiWorldPosition(pixel);
            float3 normal = GiPlaneNormal(pixel);
            float denominator = dot(direction, normal);
            float hitDistance = dot(surface - origin, normal) / denominator;
            if (denominator < -1e-4 && hitDistance > max(previousDistance, 0.015) && hitDistance <= distance)
            {
                float4 hitClip = mul(float4(originForView + direction * hitDistance, 1.0), GiViews[view].ViewProj);
                float2 hitUv = float2(hitClip.x / hitClip.w * 0.5 + 0.5, 0.5 - hitClip.y / hitClip.w * 0.5);
                float2 delta = hitUv * float2(GiSettings.Width, GiSettings.Height) - (float2(pixel.xy) + 0.5);
                if (all(abs(delta) <= 0.75) && all(hitUv >= 0.0) && all(hitUv < 1.0))
                {
                    uint3 confirmedPixel = uint3(uint2(hitUv * float2(GiSettings.Width, GiSettings.Height)), view);
                    if (GiVisibility.Load(int4(confirmedPixel, 0)) == triangle)
                    {
                        hitPixel = confirmedPixel;
                        return true;
                    }
                }
            }
        }
        previousDistance = distance;
    }
    return false;
}

[numthreads(64, 1, 1)]
void InitializeGiScreenProbes(uint3 thread : SV_DispatchThreadID)
{
    uint count = GiProbeCount();
    if (thread.x >= count * 2u) return;
    GiProbeSurface surface = {};
    GiProbeSurfaces[thread.x] = surface;
    for (uint coefficient = 0u; coefficient < 9u; coefficient++)
        GiProbeRadiance[thread.x * 9u + coefficient] = 0.0;
}

[numthreads(64, 1, 1)]
void TraceGiScreenProbes(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    uint count = GiProbeCount();
    if (index >= count) return;
    uint3 pixel = GiProbePixel(index);
    uint current = GiSettings.HistoryLayer * count + index;
    GiProbeSurface surface = {};
    if (IsVisBufferEmpty(GiVisibility.Load(int4(pixel, 0))) || GiDiffuse.Load(int4(pixel, 0)).w == 0.0)
    {
        GiProbeSurfaces[current] = surface;
        for (uint coefficient = 0u; coefficient < 9u; coefficient++)
            GiProbeRadiance[current * 9u + coefficient] = 0.0;
        return;
    }
    float3 position = GiWorldPosition(pixel);
    float3 normal = GiPlaneNormal(pixel);
    float footprint = GiPixelFootprint(position, pixel.z);
    float2 uv = (float2(pixel.xy) + 0.5) / float2(GiSettings.Width, GiSettings.Height);
    // Both grids use the material shade's raster positions, including projection jitter.
    float2 previousUv = uv - GiMotion.Load(int4(pixel, 0));
    uint previous = 0u;
    float historyCount = 0.0;
    if (GiSettings.UseHistory != 0u && all(previousUv >= 0.0) && all(previousUv < 1.0))
    {
        previous = (GiSettings.HistoryLayer ^ 1u) * count +
            GiProbeIndex(uint2(previousUv * float2(GiSettings.Width, GiSettings.Height)), pixel.z);
        GiProbeSurface old = GiProbeSurfaces[previous];
        float3 delta = old.Position.xyz + GiViews[pixel.z].PreviousDataTranslation - position;
        float tolerance = max(0.01, footprint * 2.0);
        if (old.Position.w > 0.0 && dot(old.Normal.xyz, normal) > 0.85 &&
            abs(dot(delta, normal)) < tolerance && length(delta) < footprint * 16.0)
            historyCount = min(old.Position.w, 7.0);
    }
    float2 sequence = frac(float2(GiRandom(index + 71u), GiRandom(index + 12119u)) +
        float(GiSettings.Frame + 1u) * float2(0.7548776662, 0.5698402910));
    float z = sequence.x;
    float radius = sqrt(1.0 - z * z);
    float angle = sequence.y * (2.0 * GI_PI);
    float3 tangent = normalize(cross(normal, abs(normal.y) > 0.95 ? float3(1, 0, 0) : float3(0, 1, 0)));
    float3 direction = tangent * (radius * cos(angle)) + cross(normal, tangent) * (radius * sin(angle)) + normal * z;
    uint3 hitPixel;
    float3 radiance = 0.0;
    if (TraceGiScreen(position + normal * max(0.003, footprint * 0.25), direction, pixel.z, hitPixel))
        radiance = GiReflectedRadiance.Load(int4(hitPixel, 0)).rgb;
    surface.Position = float4(position, historyCount + 1.0);
    surface.Normal = float4(normal, 0.0);
    GiProbeSurfaces[current] = surface;
    for (uint coefficient = 0u; coefficient < 9u; coefficient++)
    {
        float3 old = historyCount > 0.0 ? GiProbeRadiance[previous * 9u + coefficient].rgb : 0.0;
        float3 sampleRadiance = radiance * (GiSh(coefficient, direction) * (2.0 * GI_PI));
        GiProbeRadiance[current * 9u + coefficient] = float4((old * historyCount + sampleRadiance) / (historyCount + 1.0), 0.0);
    }
}
