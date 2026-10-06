using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private LinkedComputePipeline? _froxelBuildPipeline;
    private LinkedComputePipeline? _froxelArgumentsPipeline;
    private LinkedComputePipeline? _froxelMarkPipeline;
    private LinkedComputePipeline? _froxelPixelPipeline;
    private GraphRegionId _froxelBuildRegion;
    private GraphBufferId _froxelArguments;
    private GraphBufferInput _frameFroxels = null!;
    private GraphBufferInput[] _froxelUniforms = [];
    private GraphPassId[] _froxelMarkPasses = [];

    private void InstallFroxels(ref RenderGraphEdit graph, VirtualShadowMapShaders shaders, GraphRegionId region)
    {
        if (!_shadowMap.Settings.MarkPagesUsingFroxels || _shadowMap.Settings.HighQualityFrontLayerTranslucency) return;
        _froxelBuildRegion = graph.AddFrameRegion("Virtual shadow HiZ froxels", region);
        _frameFroxels = graph.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite);
        _froxelUniforms = [graph.CreateBufferInput(BufferUsages.Constant), graph.CreateBufferInput(BufferUsages.Constant)];
        _froxelArguments = graph.CreatePersistentBuffer(new BufferDesc(64,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.Indirect,
            "Virtual shadow froxel counts and dispatch"));
        _froxelBuildPipeline = LinkedComputePipeline.Create(_backend,_device,shaders.FroxelBuildShader,shaders.FroxelBuild,"Build virtual shadow froxels");
        _froxelArgumentsPipeline = LinkedComputePipeline.Create(_backend,_device,shaders.FroxelArgumentsShader,shaders.FroxelArguments,"Finish virtual shadow froxel dispatch");
        _froxelMarkPipeline = LinkedComputePipeline.Create(_backend,_device,shaders.FroxelMarkShader,shaders.FroxelMark,"Mark virtual shadow froxels");
        _froxelPixelPipeline = LinkedComputePipeline.Create(_backend,_device,shaders.FroxelPixelShader,shaders.FroxelPixel,"Mark virtual shadow froxel pixel receivers");
        _froxelMarkPasses =
        [
            AddIndirectPass(ref graph,"Mark virtual shadow opaque froxels",_froxelMarkPipeline,_froxelArguments,0,region),
            AddIndirectPass(ref graph,"Mark virtual shadow water froxels",_froxelMarkPipeline,_froxelArguments,16,region),
            AddIndirectPass(ref graph,"Mark virtual shadow pixel layers",_froxelPixelPipeline,_froxelArguments,48,region),
        ];
        for (int index = 0; index < _froxelMarkPasses.Length; index++)
        {
            GraphPassId id = _froxelMarkPasses[index];
            PassDefinition pass = graph.GetPass(id);
            BindPageMarking(ref graph,ref pass,_demandUniforms);
            _ = pass.Bind(_froxelUniforms[index == 1 ? 1 : 0],PipelineSync.ComputeShading);
            _ = pass.Bind(graph.CreateBufferSrv(_frameFroxels,structureStride:8),PipelineSync.ComputeShading,BufferContentScope.CountBoundedPrefix);
            _ = pass.Bind(graph.CreateBufferSrv(_froxelArguments,structureStride:4),PipelineSync.ComputeShading);
            graph.OrderAfter(id,_initializePass);
            graph.OrderAfter(id,_requestResetPass);
            graph.OrderAfter(id,_receiverMaskResetPass);
            graph.OrderAfter(id,_dynamicResolutionResetPass);
            graph.OrderAfter(_poolPreparePass,id);
        }
    }

    private void UpdateFroxels(ref RenderGraphFrame graph, in RenderView view, in ClusterPipelineOutputs geometry)
    {
        if (_froxelBuildPipeline is null) return;
        bool enabled = _viewCount != 0;
        foreach (GraphPassId pass in _froxelMarkPasses) graph.SetPassEnabled(pass,enabled);
        graph.SetPassEnabled(_markPass,false);
        if (!enabled) return;
        Matrix4x4.Invert(view.View,out Matrix4x4 viewToWorld);
        Matrix4x4 projection = view.Projection;
        bool orthographic = projection.M34 == 0f;
        float radius = MathF.Min(16f / (view.ViewportWidth * projection.M11),16f / (view.ViewportHeight * projection.M22));
        var parameters = new VirtualShadowFroxelParameters
        {
            ViewToWorld = viewToWorld,
            ViewTileScaleBias = new(16f/(view.ViewportWidth*projection.M11),-16f/(view.ViewportHeight*projection.M22),-1f/projection.M11,1f/projection.M22),
            Near = projection.M43/projection.M33,
            Radius = radius,
            DepthScale1 = 1f + MathF.Abs(projection.M34) * radius,
            ClipToViewScale = orthographic ? -1f/projection.M33 : 1f/projection.M43,
            ClipToViewBias = orthographic ? projection.M43/projection.M33 : projection.M33/projection.M43,
            Orthographic = orthographic ? 1u : 0u,
            Width = view.ViewportWidth, Height = view.ViewportHeight,
            AdditionalReceiverLayerCount = _frameAdditionalReceiverCount, Enabled = 1,
        };
        byte[] bytes = new byte[256];
        MemoryMarshal.Write(bytes,in parameters);
        GraphBufferSlice uniforms = graph.Upload<byte>(bytes,BufferUsages.Constant,"Virtual shadow froxel view");
        graph.SetBufferInput(_froxelUniforms[0],uniforms);
        parameters.Water = 1;
        byte[] waterBytes = new byte[256];
        MemoryMarshal.Write(waterBytes,in parameters);
        graph.SetBufferInput(_froxelUniforms[1],graph.Upload<byte>(waterBytes,BufferUsages.Constant,"Virtual shadow water froxel view"));
        ulong capacity = checked((ulong)view.ViewportWidth * view.ViewportHeight * (1u + _frameAdditionalReceiverCount));
        GraphBufferId froxels = graph.CreateBuffer(new BufferDesc(capacity * 8u,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite,"Virtual shadow frame froxels"));
        graph.SetBufferInput(_frameFroxels,new(froxels,BufferRange.Whole));
        GraphPassId reset = graph.AddCopyPass("Reset virtual shadow froxel counts",PassQueueSelection.AnyOfType(QueueType.Graphics),
            new VirtualShadowBufferResetPassData(_froxelArguments),default,
            VirtualShadowBufferResetPassData.Declare,VirtualShadowBufferResetPassData.Record,_froxelBuildRegion);
        GraphPassId build = graph.AddComputePass("Build virtual shadow HiZ froxels",PassQueueSelection.AnyOfType(QueueType.Graphics),
            new VirtualShadowDispatchPassData(new(DivideRoundUp(view.ViewportWidth,8),DivideRoundUp(view.ViewportHeight,8),1)),default,
            _froxelBuildPipeline.Pipeline,_froxelBuildPipeline.ParameterLayout,default,
            static (ref PassDefinition _,ref VirtualShadowDispatchPassData _) => { },VirtualShadowDispatchPassData.Record,_froxelBuildRegion);
        BindFroxelBuild(ref graph,graph.GetPass(build),uniforms,geometry,froxels);
        graph.OrderAfter(build,reset);
        GraphPassId finish = graph.AddComputePass("Finish virtual shadow froxel arguments",PassQueueSelection.AnyOfType(QueueType.Graphics),
            new VirtualShadowDispatchPassData(new(1,1,1)),default,
            _froxelArgumentsPipeline!.Pipeline,_froxelArgumentsPipeline.ParameterLayout,default,
            static (ref PassDefinition _,ref VirtualShadowDispatchPassData _) => { },VirtualShadowDispatchPassData.Record,_froxelBuildRegion);
        BindFroxelBuild(ref graph,graph.GetPass(finish),uniforms,geometry,froxels);
        graph.OrderAfter(finish,build);
    }

    private void BindFroxelBuild(ref RenderGraphFrame graph, PassDefinition pass, GraphBufferSlice uniforms,
        in ClusterPipelineOutputs geometry, GraphBufferId froxels)
    {
        _ = pass.Bind(graph.CreateBufferCbv(uniforms),PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateTextureSrv(geometry.CurrentHiZ,new(0,1,0,1,TextureAspects.Color),Format.R32Float,
            SomeEngine.Graphics.TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(geometry.SurfaceReceivers,structureStride:32),PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_additionalReceiverCount != 0 ? _additionalReceiverData :
            new GraphBufferSlice(geometry.AdditionalSurfaceReceivers,BufferRange.Whole),structureStride:32),PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(froxels,structureStride:8),PipelineSync.ComputeShading,
            GraphAccessMode.Write,WriteCoverage.Partial,resultContents:ResourceContentState.Defined,contentScope:BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_froxelArguments,structureStride:4),PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,WriteCoverage.Partial);
    }

    private void DisposeFroxelPipelines()
    {
        _froxelBuildPipeline?.Dispose(); _froxelArgumentsPipeline?.Dispose();
        _froxelMarkPipeline?.Dispose(); _froxelPixelPipeline?.Dispose();
    }
}
