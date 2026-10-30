#ifndef GI_NORMAL_SLANG
#define GI_NORMAL_SLANG

float2 EncodeGiNormal(float3 normal)
{
    normal /= abs(normal.x) + abs(normal.y) + abs(normal.z);
    float2 folded = (1.0 - abs(normal.yx)) * select(normal.xy >= 0.0, 1.0.xx, -1.0.xx);
    return normal.z >= 0.0 ? normal.xy : folded;
}

float3 DecodeGiNormal(float2 encoded)
{
    float3 normal = float3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    float fold = max(-normal.z, 0.0);
    normal.xy += select(normal.xy >= 0.0, -fold.xx, fold.xx);
    return normalize(normal);
}

uint PackGiNormal16(float3 normal)
{
    float2 oct = EncodeGiNormal(normal);
    return f32tof16(oct.x) | (f32tof16(oct.y) << 16u);
}

float3 UnpackGiNormal16(uint packed)
{
    return DecodeGiNormal(float2(f16tof32(packed), f16tof32(packed >> 16u)));
}

#endif
