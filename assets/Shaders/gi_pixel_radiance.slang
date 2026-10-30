#ifndef GI_PIXEL_RADIANCE_SLANG
#define GI_PIXEL_RADIANCE_SLANG

RWStructuredBuffer<float4> GiPixelHistory;
RWStructuredBuffer<GiPixelSurface> GiPixelSurfaces;
RWTexture2DArray<float4> GiColor;

float4 SampleGiPixelRadiance(uint3 pixel, GiProbeSurface source)
{
    int2 low = int2(floor((float2(pixel.xy) + 0.5) / 16.0 - 0.5));
    uint selected[4] = { 0u, 0u, 0u, 0u };
    float amounts[4] = { 0.0, 0.0, 0.0, 0.0 };
    float support = source.Normal.w * 0.25;
    for (uint corner = 0u; corner < 4u; corner++)
    {
        int2 tile = low + int2(corner & 1u, corner >> 1u);
        if (any(tile < 0) || any(tile >= int2(GiSettings.ProbeWidth, GiSettings.ProbeHeight))) continue;
        uint tileIndex = (pixel.z * GiSettings.ProbeHeight + uint(tile.y)) * GiSettings.ProbeWidth + uint(tile.x);
        uint2 range = GiProbeTileRange(tileIndex, GiSettings.HistoryLayer);
        for (uint item = 0u; item < range.y; item++)
        {
            uint at = GiSettings.HistoryLayer * GiProbeCount() + range.x + item;
            GiProbeSurface anchor = GiProbeSurfaces[at];
            if (anchor.Position.w == 0.0) continue;
            float agreement = max(0.0, dot(source.Normal.xyz, anchor.Normal.xyz));
            if (agreement < 0.95) continue;
            float3 delta = anchor.Position.xyz - source.Position.xyz;
            float plane = max(0.0, 1.0 - abs(dot(delta, source.Normal.xyz)) / max(0.003, source.Normal.w * 1.5));
            float weight = agreement * agreement * plane / (1.0 + dot(delta, delta) / (support * support));
            uint least = 0u;
            for (uint index = 1u; index < 4u; index++) if (amounts[index] < amounts[least]) least = index;
            if (weight > amounts[least]) { amounts[least] = weight; selected[least] = at; }
        }
    }
    float total = amounts[0] + amounts[1] + amounts[2] + amounts[3];
    if (max(max(amounts[0], amounts[1]), max(amounts[2], amounts[3])) < 1.0 / (1.0 + 16.0 * 16.0 * 16.0)) return 0.0;
    float3 normal = DecodeGiNormal(GiNormal.Load(int4(pixel, 0)).xy), result = 0.0;
    for (uint coefficient = 0u; coefficient < 9u; coefficient++)
    {
        float3 value = 0.0;
        for (uint index = 0u; index < 4u; index++)
            value += GiFilteredProbeRadiance[selected[index] * 9u + coefficient].rgb * amounts[index];
        float convolution = coefficient == 0u ? 1.0 : coefficient < 4u ? 2.0 / 3.0 : 0.25;
        result += value / total * (GiSh(coefficient, normal) * convolution);
    }
    return float4(max(result, 0.0), 1.0);
}

struct GiPixelLightingHistory
{
    float3 Irradiance;
    float Samples;
    float3 Emissive;
    float EmissiveSamples;
    float3 MovingEmissive;
    float MovingEmissiveSamples;
    uint Age;
};

GiPixelLightingHistory ReprojectGiPixelLighting(uint3 pixel, GiProbeSurface source)
{
    float3 indirect = 0.0, direct = 0.0, moving = 0.0;
    float indirectSamples = 0.0, directSamples = 0.0, movingSamples = 0.0;
    float indirectTotal = 0.0, directTotal = 0.0, movingTotal = 0.0, ageWeight = 0.0;
    uint previousAge = 255u;
    uint count = GiSettings.Width * GiSettings.Height * GiSettings.ViewCount;
    float2 uv = (float2(pixel.xy) + 0.5) / float2(GiSettings.Width, GiSettings.Height) - GiMotion.Load(int4(pixel, 0));
    if (GiSettings.UseHistory != 0u && all(uv >= 0.0) && all(uv < 1.0))
    {
        float2 coordinate = uv * float2(GiSettings.Width, GiSettings.Height) - 0.5;
        int2 low = int2(floor(coordinate)); float2 fraction = frac(coordinate);
        for (uint corner = 0u; corner < 4u; corner++)
        {
            int2 offset = int2(corner & 1u, corner >> 1u), xy = low + offset;
            if (any(xy < 0) || any(xy >= int2(GiSettings.Width, GiSettings.Height))) continue;
            uint at = (GiSettings.HistoryLayer ^ 1u) * count + (pixel.z * GiSettings.Height + uint(xy.y)) * GiSettings.Width + uint(xy.x);
            GiPixelSurface old = GiPixelSurfaces[at];
            if (old.Instance != source.Instance + 1u || dot(UnpackGiNormal16(old.Normal), source.Normal.xyz) < 0.95) continue;
            float3 local = InverseTransformInstancePosition(old.Position, GetPreviousInstanceTransform(source.Instance));
            float3 position = TransformInstanceLocalPosition(local, GetCurrentInstanceTransform(source.Instance));
            if (distance(position, source.Position.xyz) > source.Normal.w * 1.5) continue;
            float2 amount = select(offset != 0, fraction, 1.0 - fraction);
            float weight = amount.x * amount.y;
            if (weight > ageWeight) { previousAge = old.Age; ageWeight = weight; }
            uint3 previousPixel = uint3(xy, (GiSettings.HistoryLayer ^ 1u) * GiSettings.ViewCount + pixel.z);
            float4 oldIndirect = GiPixelHistory[at], oldDirect = GiDirectHistory[previousPixel], oldMoving = GiMovingDirectHistory[previousPixel];
            if (oldIndirect.w > 0.0) { indirect += oldIndirect.rgb * weight; indirectSamples += oldIndirect.w * weight; indirectTotal += weight; }
            if (oldDirect.w > 0.0) { direct += oldDirect.rgb * weight; directSamples += oldDirect.w * weight; directTotal += weight; }
            if (oldMoving.w > 0.0) { moving += oldMoving.rgb * weight; movingSamples += oldMoving.w * weight; movingTotal += weight; }
        }
    }
    GiPixelLightingHistory result;
    result.Irradiance = indirect / max(indirectTotal, 1e-20);
    result.Samples = min(indirectSamples / max(indirectTotal, 1e-20), GiSettings.RadianceChanged != 0u ? 4.0 : 16.0);
    result.Emissive = direct / max(directTotal, 1e-20);
    result.EmissiveSamples = directSamples / max(directTotal, 1e-20);
    result.MovingEmissive = moving / max(movingTotal, 1e-20);
    result.MovingEmissiveSamples = movingSamples / max(movingTotal, 1e-20);
    result.Age = previousAge;
    return result;
}
float3 ReadGiScreenDirectRadiance(uint3 pixel)
{
    GiProbeSurface source = ReadGiProbeSurface(pixel);
    GiPixelLightingHistory history = ReprojectGiPixelLighting(pixel, source);
    return GiReflectedRadiance.Load(int4(pixel, 0)).rgb + GiDiffuse.Load(int4(pixel, 0)).rgb * (history.Emissive + history.MovingEmissive);
}

float3 ReadGiScreenReflectedRadiance(uint3 pixel)
{
    GiProbeSurface source = ReadGiProbeSurface(pixel);
    GiPixelLightingHistory history = ReprojectGiPixelLighting(pixel, source);
    return GiReflectedRadiance.Load(int4(pixel, 0)).rgb + GiDiffuse.Load(int4(pixel, 0)).rgb * (history.Irradiance + history.Emissive + history.MovingEmissive);
}
[shader("compute")]
[numthreads(64, 1, 1)]
void IntegrateGiPixelRadiance(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x, count = GiSettings.Width * GiSettings.Height * GiSettings.ViewCount;
    if (index >= count) return;
    uint3 pixel = GiSurfacePixel(index);
    uint current = GiSettings.HistoryLayer * count + index;
    GiProbeSurface source = ReadGiProbeSurface(pixel);
    if (source.Position.w == 0.0)
    {
        GiPixelHistory[current] = 0.0;
        GiPixelSurfaces[current] = (GiPixelSurface)0;
        uint3 output = uint3(pixel.xy, GiSettings.HistoryLayer * GiSettings.ViewCount + pixel.z);
        GiDirectHistory[output] = 0.0; GiMovingDirectHistory[output] = 0.0;
        return;
    }
    float4 sample = SampleGiPixelRadiance(pixel, source);
    GiPixelLightingHistory history = ReprojectGiPixelLighting(pixel, source);
    float3 previousLight = history.Irradiance;
    float previousCount = history.Samples;
    uint previousAge = history.Age;
    float nextCount = previousCount + sample.w;
    float3 irradiance = (previousLight * previousCount + sample.rgb * sample.w) / max(nextCount, 1.0);
    GiPixelHistory[current] = float4(irradiance, nextCount);
    uint age = sample.w > 0.0 ? 0u : min(previousAge + 1u, 255u);
    GiPixelSurface geometry = (GiPixelSurface)0;
    geometry.Position = source.Position.xyz; geometry.Normal = PackGiNormal16(source.Normal.xyz);
    geometry.Instance = source.Instance + 1u; geometry.Age = age;
    GiPixelSurfaces[current] = geometry;
    if (sample.w == 0.0)
    {
        uint bits = firstbithigh(count) + 1u;
        uint priority = uint(min(float(previousAge) / 16.0, 1.0) * float((1u << (32u - bits)) - 1u));
        InterlockedMax(GiProbeRequests[GiSettings.HistoryLayer * GiTileCount(GiSettings) + source.Tile],
            (priority << bits) | (index + 1u));
    }
    float viewHistory = GiViewChanged(pixel.z) ? 16.0 : 1024.0;
    bool receiverMoving = (GiInstanceMovement[source.Instance] & 2u) != 0u;
    bool emissionChanges = (GiEmitterChanges[0] & 4u) != 0u;
    float directSamples = min(history.EmissiveSamples, receiverMoving || emissionChanges ? 4.0 : viewHistory);
    float movingSamples = min(history.MovingEmissiveSamples, GiSettings.SceneChanged != 0u || emissionChanges ? 4.0 : viewHistory);
    float3 direct = (history.Emissive * directSamples + GiDirectFilteredValues.Load(int4(pixel, 0)).rgb) / (directSamples + 1.0);
    float3 moving = (history.MovingEmissive * movingSamples + GiDirectMovingFilteredValues.Load(int4(pixel, 0)).rgb) / (movingSamples + 1.0);
    uint3 output = uint3(pixel.xy, GiSettings.HistoryLayer * GiSettings.ViewCount + pixel.z);
    GiDirectHistory[output] = float4(direct, directSamples + 1.0);
    GiMovingDirectHistory[output] = float4(moving, movingSamples + 1.0);
    float3 reflected = GiDiffuse.Load(int4(pixel, 0)).rgb * (irradiance + direct + moving);
    GiColor[pixel] = float4(GiColor[pixel].rgb + reflected, 1.0);
}

#endif
