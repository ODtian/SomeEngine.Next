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
RWStructuredBuffer<uint> GiProbeRequests;
Texture2DArray<uint> GiSurfaceInstances;
static const float GI_PI = 3.14159265359;

uint GiProbeCount() { return GiProbeCapacity(GiSettings); }

float3 GiWorldPosition(uint3 pixel)
{
    float2 uv = (float2(pixel.xy) + 0.5) / float2(GiSettings.Width, GiSettings.Height);
    float4 world = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0,
        GiDepth.Load(int4(pixel, 0)), 1.0), GiViews[pixel.z].ClipToWorld);
    return world.xyz / world.w;
}

float GiPixelFootprint(float3 position, uint view)
{
    return max(0.002, length(position - GiViews[view].CameraPos) *
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
bool TraceGiScreen(float3 origin, float3 direction, uint view, out uint3 hitPixel, out float rayDistance)
{
    hitPixel = 0u;
    rayDistance = 24.0;
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
        if (!IsVisBufferEmpty(triangle) && GiSurfaceInstances.Load(int4(pixel, 0)) != 0u)
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
                        rayDistance = hitDistance;
                        return true;
                    }
                }
            }
        }
        previousDistance = distance;
    }
    return false;
}

#include "gi_probe_layout.slang"

[shader("compute")]
[numthreads(64, 1, 1)]
void InitializeGiScreenProbes(uint3 thread : SV_DispatchThreadID)
{
    uint count = GiProbeCount();
    if (thread.x >= count * 2u) return;
    if (thread.x < GiTileCount(GiSettings) * 2u) GiProbeRequests[thread.x] = 0u;
    GiProbeSurfaces[thread.x] = (GiProbeSurface)0;
    GiProbeSamples[thread.x] = 0.0;
    for (uint coefficient = 0u; coefficient < 9u; coefficient++)
    {
        GiProbeRadiance[thread.x * 9u + coefficient] = 0.0;
        GiFilteredProbeRadiance[thread.x * 9u + coefficient] = 0.0;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void TraceGiScreenProbes(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiProbeCount()) return;
    GiRays[index] = (GiTraceRay)0;
    GiScreenHits[index] = float4(0.0, 0.0, 0.0, GiScene.TraceDistance);
    uint current = GiSettings.HistoryLayer * GiProbeCount() + index;
    GiProbeSurface source = GiProbeSurfaces[current];
    if (source.Position.w == 0.0 || GiProbeSamples[current].y == 0.0) return;
    uint view = source.Tile / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    float2 rotation = float2(GiRandom(source.Seed + 71u), GiRandom(source.Seed + 12119u));
    float2 sequence = frac(rotation + float(GiSettings.Frame + 1u) * float2(0.7548776662, 0.5698402910));
    float z = sequence.x, radius = sqrt(1.0 - z * z), angle = sequence.y * (2.0 * GI_PI);
    float3 normal = source.Normal.xyz;
    float3 tangent = normalize(cross(normal, abs(normal.y) > 0.95 ? float3(1, 0, 0) : float3(0, 1, 0)));
    float3 direction = tangent * (radius * cos(angle)) + cross(normal, tangent) * (radius * sin(angle)) + normal * z;
    GiTraceRay ray;
    ray.Origin = source.Position.xyz + normal * 0.012;
    ray.Direction = direction; ray.Start = 0.002; ray.Limit = GiScene.TraceDistance;
    GiRays[index] = ray;
    uint3 hitPixel;
    float hitDistance;
    bool screen = TraceGiScreen(ray.Origin, direction, view, hitPixel, hitDistance);
    GiScreenHits[index] = float4(float3(hitPixel), screen ? hitDistance : GiScene.TraceDistance);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void IntegrateGiScreenProbes(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiProbeCount()) return;
    uint current = GiSettings.HistoryLayer * GiProbeCount() + index;
    float2 samples = GiProbeSamples[current];
    if (samples.y == 0.0) return;
    float3 radiance = GiProbeIncoming[index].rgb, direction = GiRays[index].Direction;
    for (uint coefficient = 0u; coefficient < 9u; coefficient++)
    {
        float3 old = GiProbeRadiance[current * 9u + coefficient].rgb;
        float3 value = radiance * (GiSh(coefficient, direction) * (2.0 * GI_PI));
        GiProbeRadiance[current * 9u + coefficient] = float4((old * samples.x + value) / (samples.x + samples.y), 0.0);
    }
    GiProbeSamples[current] = float2(samples.x + samples.y, samples.y);
}

#include "gi_pipeline.slang"
#include "gi_probe_history.slang"
#include "gi_emissive_lights.slang"
#include "gi_pixel_radiance.slang"
