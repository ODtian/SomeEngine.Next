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
        uint[] uniforms = [1,1,1,0,8,2,0,1,0,1,BitConverter.SingleToUInt32Bits(.87f),1];
        uint[] projections = MemoryMarshal.Cast<byte, uint>(MemoryMarshal.AsBytes(new[] { Matrix4x4.Identity }.AsSpan())).ToArray();
        projections = [.. projections, uint.MaxValue, 0, 0, 0];
        BufferInput[] buffers =
        [
            new("Prefilter", uniforms, ResourceBindingType.ConstantBuffer, 0),
            new("FilterViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new("PreviousFilterViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new("FilterProjections", projections, ResourceBindingType.BufferSrv, 80),
            new("FilterPageReferences", [0,uint.MaxValue,uint.MaxValue,uint.MaxValue], ResourceBindingType.BufferSrv, 4),
            new("FilterPageTable", [VirtualShadowPageEntry.Encode(0, 2)], ResourceBindingType.BufferSrv, 4),
            new("FilterPreviousPageTable", [VirtualShadowPageEntry.Encode(0, 2),0], ResourceBindingType.BufferUav, 4),
            new("FilterRequestFlags", [4], ResourceBindingType.BufferSrv, 4),
            new("FilterRequestFrames", [0], ResourceBindingType.BufferSrv, 4),
            new("FilterPages", new uint[4], ResourceBindingType.BufferSrv, 4),
            new("FilterPageCount", new uint[24], ResourceBindingType.BufferSrv, 0),
            new("SelectedFilterPages", new uint[4], ResourceBindingType.BufferUav, 4),
            new("FilterArguments", new uint[24], ResourceBindingType.BufferUav, 0),
        ];
        uint[] depth = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.1f), 256).ToArray();
        depth[4 * 16 + 4] = BitConverter.SingleToUInt32Bits(.9f);
        SampledImage[] images =
        [
            new(16,16,depth,0, ViewDimension: TextureViewDimension.Texture2DArray, Name: "FilterDepthAtlas"),
            new(16,16,new uint[256],0,Format.R8UNorm, Type: ResourceBindingType.TextureUav, Name: "FilterAxisX"),
            new(20,20,new uint[800],0,Format.R32Float, TextureViewDimension.Texture2DArray, ResourceBindingType.TextureUav, 2, "FilterSeparate"),
            new(20,20,new uint[400],0,Format.R32Float, Type: ResourceBindingType.TextureUav, Name: "FilterMerged"),
            new(2,3,Enumerable.Repeat(0xffffu,6).ToArray(),0,Name: "FilterReceiverMasks"),
            new(2,3,Enumerable.Repeat(0xffffu,12).ToArray(),0,ViewDimension: TextureViewDimension.Texture2DArray,
                Type: ResourceBindingType.TextureUav, Layers: 2, Name: "FilterPreviousReceiverMasks"),
        ];
        var compute = new D3D12TestCompute(compiler);
        string[] workNames = ["Prefilter", "FilterViews", "FilterPageReferences", "FilterPageTable",
            "FilterPreviousPageTable", "FilterRequestFlags", "FilterRequestFrames", "SelectedFilterPages", "FilterArguments"];
        BufferInput[] workBuffers = buffers.Where(input => workNames.Contains(input.Name)).ToArray();
        uniforms[6] = 6;
        uint[][] reset = compute.Execute("vsm_prefilter_work.slang", "ResetVirtualShadowFilterWork", new(1,1,1), workBuffers);
        workBuffers[^1] = workBuffers[^1] with { Data = reset[2] };
        uniforms[6] = 7;
        uint[][] selected = compute.Execute("vsm_prefilter_work.slang", "SelectVirtualShadowFilterPages", new(1,1,1), workBuffers);
        buffers[^4] = buffers[^4] with { Data = selected[1] };
        buffers[^3] = buffers[^3] with { Data = selected[2] };
        string[] workOnly = ["FilterRequestFlags", "FilterRequestFrames", "SelectedFilterPages", "FilterArguments"];
        buffers = buffers.Where(input => !workOnly.Contains(input.Name)).ToArray();
        uniforms[6] = 0;
        uint[][] horizontal = compute.ExecuteImages("vsm_prefilter.slang", "PrefilterVirtualShadowDistant", new(1,1,1), buffers, images);
        images[1] = images[1] with { Pixels = horizontal[1] };
        uniforms[6] = 1;
        uint[][] filtered = compute.ExecuteImages("vsm_prefilter.slang", "PrefilterVirtualShadowDistant", new(1,1,1), buffers, images);
        float visibility = BitConverter.UInt32BitsToSingle(filtered[2][400 + 5 * 20 + 5]);
        // Native R8 rounds the X-axis center .625 to 159/255; the Y-axis
        // six-weight contribution yields the known 219/255 visibility.
        Assert.InRange(visibility, .858823f, .858825f);
        projections[16] = 0;
        uniforms[8] = 1;
        uint[] history = new uint[800];
        Array.Fill(history, BitConverter.SingleToUInt32Bits(.8587f), 0, 400);
        images[2] = images[2] with { Pixels = history };
        uint[][] blended = compute.ExecuteImages("vsm_prefilter.slang", "PrefilterVirtualShadowDistant", new(1,1,1), buffers, images);
        float temporalVisibility = BitConverter.UInt32BitsToSingle(blended[2][400 + 5 * 20 + 5]);
        Assert.InRange(temporalVisibility, .862621f, .862623f);
    }
}
