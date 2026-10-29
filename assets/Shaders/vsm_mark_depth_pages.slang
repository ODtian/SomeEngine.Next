#define VSM_WHOLE_PAGE_SAMPLING 1
#include "cluster_view.slang"
#include "vsm_receiver_requests.slang"

StructuredBuffer<ClusterViewUniforms> ReceiverViews;
Texture2DArray<float> ReceiverDepth;

[shader("compute")]
[numthreads(8, 8, 1)]
void MarkVirtualShadowDepthPages(uint3 pixel : SV_DispatchThreadID)
{
    ClusterViewUniforms view = ReceiverViews[pixel.z];
    if (pixel.x >= view.ScreenWidth || pixel.y >= view.ScreenHeight) return;
    float depth = ReceiverDepth.Load(int4(pixel, 0));
    if (depth < 0.0 || depth >= 1.0) return;
    float2 uv = (float2(pixel.xy) + .5) / float2(view.ScreenWidth, view.ScreenHeight);
    float4 world = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0), view.ClipToWorld);
    float3 position = abs(world.w) > 1e-8 ? world.xyz / world.w : world.xyz;
    MarkVirtualShadowPages(position, pixel.z);
}
