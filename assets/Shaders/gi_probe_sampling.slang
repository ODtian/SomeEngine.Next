#ifndef GI_PROBE_SAMPLING_SLANG
#define GI_PROBE_SAMPLING_SLANG
#include "cluster_view.slang"
#include "gi_probe_data.slang"

ConstantBuffer<GiScreenSettings> GiSettings;
StructuredBuffer<GiProbeSurface> GiPreviousProbeSurfaces;
StructuredBuffer<float4> GiPreviousProbeRadiance;

float3 SampleGiDiffuse(uint3 pixel, float2 motion, float3 position, float3 normal,
    float3 plane, float3 diffuse, ClusterViewUniforms view)
{
    if (GiSettings.UseHistory == 0u) return 0.0;
    float2 previousUv = (float2(pixel.xy) + 0.5) / float2(GiSettings.Width, GiSettings.Height) - motion;
    if (any(previousUv < 0.0) || any(previousUv >= 1.0)) return 0.0;
    float3 viewPosition = ClusterPositionForView(position, view);
    float footprint = max(0.001, abs(mul(float4(viewPosition, 1.0), view.View).z) *
        2.0 / (abs(view.P11) * float(GiSettings.Height)));
    float2 coordinate = (previousUv * float2(GiSettings.Width, GiSettings.Height) - 0.5 -
        float(GI_PROBE_SPACING / 2u)) / float(GI_PROBE_SPACING);
    int2 lower = int2(floor(coordinate));
    float2 fraction = frac(coordinate);
    uint count = GiSettings.ProbeWidth * GiSettings.ProbeHeight * GiSettings.ViewCount;
    float3 reflection = 0.0;
    float total = 0.0;
    for (uint corner = 0u; corner < 4u; corner++)
    {
        int2 offset = int2(corner & 1u, corner >> 1u);
        int2 probe = lower + offset;
        if (any(probe < 0) || any(probe >= int2(GiSettings.ProbeWidth, GiSettings.ProbeHeight))) continue;
        uint index = (GiSettings.HistoryLayer ^ 1u) * count +
            (pixel.z * GiSettings.ProbeHeight + uint(probe.y)) * GiSettings.ProbeWidth + uint(probe.x);
        GiProbeSurface anchor = GiPreviousProbeSurfaces[index];
        if (anchor.Position.w == 0.0) continue;
        float3 delta = anchor.Position.xyz + view.PreviousDataTranslation - position;
        float planeError = abs(dot(delta, plane)) / max(0.003, footprint * 1.5);
        float normalWeight = pow(saturate(dot(anchor.Normal.xyz, plane)), 8.0);
        float2 amount = select(offset != 0, fraction, 1.0 - fraction);
        float weight = amount.x * amount.y * normalWeight / (1.0 + planeError * planeError);
        float3 incoming = 0.0;
        for (uint coefficient = 0u; coefficient < 9u; coefficient++)
        {
            float convolution = coefficient == 0u ? 1.0 : (coefficient < 4u ? 2.0 / 3.0 : 0.25);
            incoming += GiPreviousProbeRadiance[index * 9u + coefficient].rgb *
                (GiSh(coefficient, normal) * convolution);
        }
        reflection += incoming * weight;
        total += weight;
    }
    return total > 0.0 ? max(reflection / total, 0.0) * diffuse : 0.0;
}

#endif
