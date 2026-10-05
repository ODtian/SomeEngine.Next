Texture2DArray<float> CopySourceHiZ;
RWTexture2DArray<float> CopyDestinationHiZ;

[shader("compute")]
[numthreads(8, 8, 1)]
void CopyVirtualShadowPageHiZ(uint3 pixel : SV_DispatchThreadID)
{
    uint width, height, layers;
    CopyDestinationHiZ.GetDimensions(width, height, layers);
    if (pixel.x < width && pixel.y < height && pixel.z < layers)
        CopyDestinationHiZ[pixel] = CopySourceHiZ.Load(int4(pixel, 0));
}
