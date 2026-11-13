__exported import vsm_visibility_requests;
__exported import cluster_structures;
__exported import visbuffer_encoding;
#include "cluster_instance_properties.slang"

struct VirtualShadowVisibilityRequestParameters
{
    uint Width, Height, ViewCount, Padding;
};

ConstantBuffer<VirtualShadowVisibilityRequestParameters> VirtualShadowVisibilityRequests;
Texture2DArray<uint> VisBuffer;
Texture2DArray<float> DepthBuffer;
StructuredBuffer<ClusterViewUniforms> Views;
StructuredBuffer<VisibleCluster> VisibleClusters;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;

[shader("compute")]
[numthreads(8, 8, 1)]
void RequestOpaqueShadowPages(uint3 pixel : SV_DispatchThreadID)
{
    if (pixel.x >= VirtualShadowVisibilityRequests.Width ||
        pixel.y >= VirtualShadowVisibilityRequests.Height || pixel.z >= VirtualShadowVisibilityRequests.ViewCount) return;
    uint visibility = VisBuffer[pixel];
    if (IsVisBufferEmpty(visibility)) return;
    DrawRequest cluster = VisibleClusterRequest(VisibleClusters[DecodeVisBufferVisibleClusterIndex(visibility)]);
    uint2 firstPerson = LoadInstanceFirstPerson(InstanceData, InstanceProperties, cluster.InstanceID);
    ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties,
        cluster.InstanceID, Views[pixel.z]);
    RequestVirtualShadowOpaqueVisibility(DepthBuffer, pixel,
        uint2(VirtualShadowVisibilityRequests.Width, VirtualShadowVisibilityRequests.Height),
        view.ClipToWorld, (firstPerson.x & 3u) == 1u, VirtualShadowLightViewIndices, view.WorldTranslation);
}
