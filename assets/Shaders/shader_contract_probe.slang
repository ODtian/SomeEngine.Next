import resource_effects;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2D<float4> outputTexture : register(u1, space0);
[shader("compute")]
[numthreads(1, 1, 1)]
void Main(
    [ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
    uniform Texture2DArray<float4> sourceTexture,
    uint3 dispatchThreadId : SV_DispatchThreadID)
{
    outputTexture[dispatchThreadId.xy] = sourceTexture.Load(int4(dispatchThreadId.xy, 0, 0));
}
