using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowPrefilteredDistantTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void SinglePixelDistantOcclusionRetainsFilteredCoverageAndTemporalMinimumStep()
    {
        var view = new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 8, PageSize = 8, AtlasSize = 16,
            PhysicalPagesPerRow = 2, MaxPhysicalPages = 4, CacheGeneration = 7, MipLevelCount = 1,
            ClipmapLevelCount = 1, WorldUnitsPerCentimeter = 100,
            Flags = VirtualShadowViewFlags.PrefilteredDistant, DepthCullRange = new Vector2(.5f),
        };
        uint[] views = MemoryMarshal.Cast<byte, uint>(MemoryMarshal.AsBytes(new[] { view }.AsSpan())).ToArray();
        uint[] uniforms = [1,1,1,16,8,2,0,1,0,1,BitConverter.SingleToUInt32Bits(.87f),0];
        uint[] projections = MemoryMarshal.Cast<byte, uint>(MemoryMarshal.AsBytes(new[] { Matrix4x4.Identity }.AsSpan())).ToArray();
        projections = [.. projections, uint.MaxValue, 0, 0, 0];
        BufferInput[] buffers =
        [
            new("Prefilter", uniforms, ResourceBindingType.ConstantBuffer, 0),
            new("FilterViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new("PreviousFilterViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new("FilterProjections", projections, ResourceBindingType.BufferSrv, 80),
            new("FilterPageReferences", [0,uint.MaxValue,uint.MaxValue,uint.MaxValue], ResourceBindingType.BufferSrv, 4),
            new("FilterPageTable", [1,0,0,7], ResourceBindingType.BufferSrv, 16),
            new("FilterReceiverMasks", [uint.MaxValue,uint.MaxValue], ResourceBindingType.BufferSrv, 8),
            new("FilterPreviousPageTable", [1,0,0,7,0,0,0,0], ResourceBindingType.BufferUav, 16),
            new("FilterPreviousReceiverMasks", [uint.MaxValue,uint.MaxValue,0,0], ResourceBindingType.BufferUav, 8),
        ];
        uint[] depth = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.1f), 256).ToArray();
        depth[4 * 16 + 4] = BitConverter.SingleToUInt32Bits(.9f);
        SampledImage[] images =
        [
            new(16,16,depth,0, Name: "FilterDepthAtlas"),
            new(16,16,new uint[256],0,Format.R32Float, Type: ResourceBindingType.TextureUav, Name: "FilterAxisX"),
            new(20,20,new uint[800],0,Format.R32Float, TextureViewDimension.Texture2DArray, ResourceBindingType.TextureUav, 2, "FilterSeparate"),
            new(20,20,new uint[400],0,Format.R32Float, Type: ResourceBindingType.TextureUav, Name: "FilterMerged"),
        ];
        var compute = new D3D12TestCompute(compiler);
        uint[][] horizontal = compute.ExecuteImages("vsm_prefilter.slang", "PrefilterVirtualShadowDistant", new(2,2,1), buffers, images);
        images[1] = images[1] with { Pixels = horizontal[2] };
        uniforms[6] = 1;
        uint[][] filtered = compute.ExecuteImages("vsm_prefilter.slang", "PrefilterVirtualShadowDistant", new(2,2,1), buffers, images);
        float visibility = BitConverter.UInt32BitsToSingle(filtered[3][400 + 5 * 20 + 5]);
        // A single blocked texel removes (6/16)^2 of the two-axis footprint.
        Assert.InRange(visibility, .859374f, .859376f);
        projections[16] = 0;
        uniforms[8] = 1;
        uint[] history = new uint[800];
        Array.Fill(history, BitConverter.SingleToUInt32Bits(.8592f), 0, 400);
        images[2] = images[2] with { Pixels = history };
        uint[][] blended = compute.ExecuteImages("vsm_prefilter.slang", "PrefilterVirtualShadowDistant", new(2,2,1), buffers, images);
        float temporalVisibility = BitConverter.UInt32BitsToSingle(blended[3][400 + 5 * 20 + 5]);
        Assert.InRange(temporalVisibility, .863121f, .863122f);
    }
}
