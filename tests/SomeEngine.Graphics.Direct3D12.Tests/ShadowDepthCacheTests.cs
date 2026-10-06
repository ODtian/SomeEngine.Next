using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowDepthCacheTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void DynamicDepthStartsFromReusableStaticDepth()
    {
        uint[][] result = new D3D12TestCompute(compiler).ExecuteSource("""
            #include "vsm_depth_cache.slang"
            RWStructuredBuffer<uint> Depth;
            [shader("compute")]
            [numthreads(1,1,1)]
            void InitializeDepth()
            {
                Depth[0] = InitializeVirtualShadowDynamicDepth(asuint(.75), VSM_PAGE_DYNAMIC_UNCACHED);
                Depth[1] = InitializeVirtualShadowDynamicDepth(asuint(.75), VSM_PAGE_STATIC_UNCACHED | VSM_PAGE_DYNAMIC_UNCACHED);
            }
            """, "InitializeDepth", new(1, 1, 1), [(new uint[2], ResourceBindingType.BufferUav, 4)]);
        Assert.Equal(.75f, BitConverter.UInt32BitsToSingle(result[0][0]));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(result[0][1]));
    }
}
