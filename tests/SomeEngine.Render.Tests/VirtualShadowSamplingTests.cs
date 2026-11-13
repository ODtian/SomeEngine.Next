using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowSamplingTests(VirtualShadowGpuResources gpuResources)
{
    [Theory]
    [InlineData(0, .3f)]
    [InlineData(2, .5f)]
    [InlineData(4, .7f)]
    public void ReceiverReconstructionPreservesPixelCoordinatesAndFootprint(uint pixelX,float depth)
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("receiver_projection","""
            #include "vsm_receiver_pixel.slang"
            StructuredBuffer<uint> Pixel;
            StructuredBuffer<float4x4> ClipToWorld;
            Texture2DArray<float> ReceiverDepth;
            RWStructuredBuffer<float3> Result;
            [shader("compute")] [numthreads(1,1,1)]
            void ObserveReceiver()
            {
                uint3 pixel = uint3(Pixel[0],2u,0u);
                VirtualShadowReceiverPixel receiver = ReconstructVirtualShadowReceiverPixel(
                    ReceiverDepth,pixel,uint2(5u,5u),ReceiverDepth.Load(int4(pixel,0)),ClipToWorld[0]);
                Result[0] = receiver.Position;
                Result[1] = receiver.PositionDx;
                Result[2] = receiver.PositionDy;
            }
            """);
        Matrix4x4 transform = Matrix4x4.CreateScale(.25f,.25f,1f);
        uint[] depths = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(depth),25).ToArray();
        depths[13] = BitConverter.SingleToUInt32Bits(1f);
        VirtualShadowGpuGraph.Readback<Vector3> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphTextureId image = scene.Image(ref edit,5,5,1,depths,Format.R32Float);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<Vector3>(ref edit,3);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["Pixel"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { pixelX }),PipelineSync.ComputeShading),
                ["ClipToWorld"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { transform }),PipelineSync.ComputeShading),
                ["ReceiverDepth"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(image,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:12),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId sample = scene.Add(ref edit,shader,"ObserveReceiver",new(1,1,1),resources);
            observed = scene.Read<Vector3>(ref edit,output,3,sample);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        Vector3 position = new(((pixelX + .5f) / 5f * 2f - 1f) * .25f,0,depth);
        Vector3[] expected = [position,position + new Vector3(.1f,0,0),position + new Vector3(0,-.1f,0)];
        for (int index = 0; index < expected.Length; index++)
            Assert.InRange(Vector3.Distance(expected[index],observed.Values[index]),0f,1e-6f);
    }

    [Theory]
    [InlineData(.75f,0f,0f,0)]
    [InlineData(1.5f,0f,0f,0)]
    [InlineData(3f,0f,0f,1)]
    [InlineData(6f,0f,0f,2)]
    [InlineData(3f,1f,0f,2)]
    [InlineData(3f,-1f,0f,0)]
    [InlineData(3f,0f,10000f,1)]
    public void DirectionalSamplingUsesWorldDistanceAndResolutionBias(float distance,float bias,float origin,int selectedLevel)
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("directional_distance",SamplingResources + """
            StructuredBuffer<float3> Receivers;
            [shader("compute")] [numthreads(1,1,1)]
            void SampleDirectional()
            {
                float3 p = Receivers[0];
                Result[0] = SampleDirectionalVirtualShadowMap(Views,Pages,Atlas,0u,4u,0.0,
                    p,p + float3(.001,0,0),p + float3(0,.001,0),1u,1u,0.0,0.0);
                Result[1] = SampleDirectionalVirtualShadowMap(Views,Pages,Atlas,0u,4u,0.0,
                    p,p + float3(.08,0,0),p + float3(.08,.001,0),1u,1u,0.0,0.0);
            }
            """);
        VirtualShadowView[] views = new VirtualShadowView[4];
        uint[] pages = new uint[64], pixels = new uint[512 * 128];
        for (int level = 0; level < 4; level++)
        {
            views[level] = View(512,1,(uint)(level * 16));
            views[level].LightViewProjection = Matrix4x4.CreateScale(1f / (4 << level),1f / (4 << level),.1f);
            views[level].ClipmapLevel = level;
            views[level].ClipmapLevelCount = 4;
            views[level].ResolutionLodBias = bias;
            views[level].ClipmapWorldOrigin = new(origin,0,0);
            views[level].WorldTranslation = new(-origin,0,0);
            Array.Fill(pages,VirtualShadowPageEntry.Encode((uint)level,4),level * 16,16);
            FillPage(pixels,512,(uint)level,level == selectedLevel ? .99f : .01f);
        }
        VirtualShadowGpuGraph.Readback<float> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphTextureId atlas = scene.Image(ref edit,512,128,1,pixels);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<float>(ref edit,2);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["Views"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,views),PipelineSync.ComputeShading),
                ["Pages"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,pages),PipelineSync.ComputeShading),
                ["Receivers"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { new Vector3(origin,0,distance) }),PipelineSync.ComputeShading),
                ["Atlas"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(atlas,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:4),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId sample = scene.Add(ref edit,shader,"SampleDirectional",new(1,1,1),resources);
            observed = scene.Read<float>(ref edit,output,2,sample);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        Assert.Equal(new[] { 0f,0f },observed.Values);
    }

    [Theory]
    [InlineData(0,0f,0)]
    [InlineData(1,0f,1)]
    [InlineData(2,0f,2)]
    [InlineData(3,0f,3)]
    [InlineData(1,1f,2)]
    public void SpotSamplingUsesNominalPixelRadiusAndResolutionBias(int pixelMip,float bias,int selectedMip)
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("spot_radius",SamplingResources + """
            StructuredBuffer<float> PixelRadius;
            [shader("compute")] [numthreads(1,1,1)]
            void SampleSpot()
            {
                float3 p = float3(0,0,.5);
                Result[0] = SampleSpotVirtualShadowMap(Views,Pages,Atlas,0u,0.0,PixelRadius[0],
                    p,p + float3(.001,0,0),p + float3(0,.001,0),1u,1u,0.0,0.0);
            }
            """);
        VirtualShadowView view = View(1024,4);
        view.LightKind = VirtualShadowLightKind.Spot;
        view.ResolutionLodBias = bias;
        uint[] pages = new uint[85],pixels = new uint[512 * 128];
        int offset = 0;
        for (int mip = 0; mip < 4; mip++)
        {
            int dimension = 8 >> mip;
            Array.Fill(pages,VirtualShadowPageEntry.Encode((uint)mip,4),offset,dimension * dimension);
            offset += dimension * dimension;
            FillPage(pixels,512,(uint)mip,mip == selectedMip ? .75f : .25f);
        }
        VirtualShadowGpuGraph.Readback<float> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphTextureId atlas = scene.Image(ref edit,512,128,1,pixels);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<float>(ref edit,1);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["Views"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { view }),PipelineSync.ComputeShading),
                ["Pages"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,pages),PipelineSync.ComputeShading),
                ["PixelRadius"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { .75f * (1 << pixelMip) / 1024f }),PipelineSync.ComputeShading),
                ["Atlas"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(atlas,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:4),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId sample = scene.Add(ref edit,shader,"SampleSpot",new(1,1,1),resources);
            observed = scene.Read<float>(ref edit,output,1,sample);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        Assert.Equal(0f,Assert.Single(observed.Values));
    }

    [Fact]
    public void FilteredSamplingCombinesDepthAcrossPageBorders()
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("filtered_page_borders",SamplingResources + """
            [shader("compute")] [numthreads(1,1,1)]
            void SampleBorders()
            {
                float2 pixels[3] = { float2(64,64),float2(127.75,64),float2(127.75,127.75) };
                for (uint i = 0u; i < 3u; i++)
                {
                    float2 uv = pixels[i] / 256.0;
                    Result[i] = SampleVirtualShadowMapFiltered(Views[0],Pages,Atlas,
                        float3(uv.x * 2.0 - 1.0,1.0 - uv.y * 2.0,.5),0u,8u,16u,4.0,8.0);
                }
            }
            """);
        uint[] pixels = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.2f),512 * 256).ToArray();
        FillPage(pixels,512,0,.8f);
        FillPage(pixels,512,4,.8f);
        uint[] pages = Enumerable.Range(0,5).Select(page => VirtualShadowPageEntry.Encode((uint)page,4)).ToArray();
        VirtualShadowGpuGraph.Readback<float> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphTextureId atlas = scene.Image(ref edit,512,256,1,pixels);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<float>(ref edit,3);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["Views"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { View(256,2) }),PipelineSync.ComputeShading),
                ["Pages"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,pages),PipelineSync.ComputeShading),
                ["Atlas"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(atlas,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:4),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId sample = scene.Add(ref edit,shader,"SampleBorders",new(1,1,1),resources);
            observed = scene.Read<float>(ref edit,output,3,sample);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        Assert.Equal(0f,observed.Values[0]);
        Assert.All(observed.Values.Skip(1),visibility => Assert.InRange(visibility,1f / 16f,15f / 16f));
    }

    [Theory]
    [InlineData(VirtualShadowLightKind.Point)]
    [InlineData(VirtualShadowLightKind.Spot)]
    public void CoarseLocalDepthShadowsEveryProjectionCorner(VirtualShadowLightKind kind)
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("coarse_projection_corners",SamplingResources + """
            [shader("compute")] [numthreads(4,1,1)]
            void SampleCorners(uint3 id : SV_DispatchThreadID)
            {
                float2 uv = (float2(id.x & 1u,id.x >> 1u) * 16383.0 + .5) / 16384.0;
                Result[id.x] = SampleVirtualShadowMapFiltered(Views[0],Pages,Atlas,
                    float3(uv.x * 2.0 - 1.0,1.0 - uv.y * 2.0,.5),0u,1u,1u,0.0,0.0);
            }
            """);
        VirtualShadowView view = View(16384,8);
        view.LightKind = kind;
        uint[] pages = new uint[view.PageTableEntryCount];
        pages[^1] = VirtualShadowPageEntry.Encode(3,4);
        VirtualShadowGpuGraph.Readback<float> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphTextureId atlas = scene.Image(ref edit,512,128,1,Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.8f),512 * 128).ToArray());
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<float>(ref edit,4);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["Views"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { view }),PipelineSync.ComputeShading),
                ["Pages"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,pages),PipelineSync.ComputeShading),
                ["Atlas"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(atlas,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:4),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId sample = scene.Add(ref edit,shader,"SampleCorners",new(1,1,1),resources);
            observed = scene.Read<float>(ref edit,output,4,sample);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        Assert.Equal(new[] { 0f,0f,0f,0f },observed.Values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DirectionalSamplingUsesTheNearestResidentClipmap(int firstResident)
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("nearest_resident_clipmap",SamplingResources + """
            [shader("compute")] [numthreads(1,1,1)]
            void SampleResident()
            {
                Result[0] = SampleDirectionalVirtualShadowMap(Views,Pages,Atlas,0u,3u,0.0,
                    float3(.25,.25,.5),float3(.2501,.25,.5),float3(.25,.2501,.5),1u,1u,0.0,0.0);
            }
            """);
        VirtualShadowView[] views = new VirtualShadowView[3];
        uint[] pages = new uint[48],pixels = new uint[512 * 128];
        for (int level = 0; level < 3; level++)
        {
            views[level] = View(512,1,(uint)level * 16u);
            views[level].ClipmapLevel = level;
            views[level].ClipmapLevelCount = 3;
            if (level >= firstResident) Array.Fill(pages,VirtualShadowPageEntry.Encode((uint)level,4),level * 16,16);
            FillPage(pixels,512,(uint)level,level == firstResident ? .75f : .25f);
        }
        VirtualShadowGpuGraph.Readback<float> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphTextureId atlas = scene.Image(ref edit,512,128,1,pixels);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<float>(ref edit,1);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["Views"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,views),PipelineSync.ComputeShading),
                ["Pages"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,pages),PipelineSync.ComputeShading),
                ["Atlas"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(atlas,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:4),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId sample = scene.Add(ref edit,shader,"SampleResident",new(1,1,1),resources);
            observed = scene.Read<float>(ref edit,output,1,sample);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        Assert.Equal(firstResident == 3 ? 1f : 0f,Assert.Single(observed.Values));
    }

    private static VirtualShadowView View(uint resolution,uint mipCount,uint offset = 0) => new()
    {
        LightViewProjection = Matrix4x4.Identity,
        VirtualResolution = resolution,PageSize = 128,AtlasSize = 512,AtlasPagesPerRow = 4,MaxPages = 8,
        PageTableOffset = offset,CacheGeneration = 7,MipLevelCount = mipCount,ClipmapLevelCount = 1,
        WorldUnitsPerCentimeter = 1,RangeId = 1,RangeGeneration = 1,
    };

    private static void FillPage(uint[] pixels,uint width,uint page,float reverseDepth)
    {
        for (uint y = 0; y < 128; y++)
            Array.Fill(pixels,BitConverter.SingleToUInt32Bits(reverseDepth),
                (int)((page / 4u * 128u + y) * width + page % 4u * 128u),128);
    }

    private const string SamplingResources = """
        #define VSM_WHOLE_PAGE_SAMPLING 1
        #include "vsm_sampling.slang"
        StructuredBuffer<VirtualShadowMapParameters> Views;
        StructuredBuffer<uint> Pages;
        Texture2DArray<uint> Atlas;
        RWStructuredBuffer<float> Result;

        """;
}
