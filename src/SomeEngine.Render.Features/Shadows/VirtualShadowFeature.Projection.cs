using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private LinkedComputePipeline _projectionPipeline = null!;
    private GraphPassId _projectionPass;
    private GraphBufferInput _projectionUniforms = null!;
    private GraphBufferInput _projectionViews = null!;
    private GraphTextureInput _projectedVisibility = null!;
    private GraphTextureInput _additionalProjectedVisibility = null!;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    private struct ProjectionUniforms
    {
        public uint Width, Height, ViewCount, LightCapacity;
        public uint AdditionalReceiverLayerCount, Pad0, Pad1, Pad2;
    }

    private void InstallProjection(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders, GraphRegionId region)
    {
        _projectionPipeline = LinkedComputePipeline.Create(_backend, _device,
            shaders.ProjectLightsShader, shaders.ProjectLights, "Project virtual shadow lights");
        _projectionUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _projectionViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _projectedVisibility = edit.CreateTextureInput();
        _additionalProjectedVisibility = edit.CreateTextureInput();
        _projectionPass = AddComputePass(ref edit, "Project virtual shadow lights", _projectionPipeline,
            new VirtualShadowDispatchPassData(default), region);
        var binders = new List<GraphResourceBinder>();
        int[] resources = FeatureResourceLayout.Resolve<int>(_projectionPipeline.ParameterLayout,
            (name, type) =>
            {
                GraphResourceBinder? binder = (name, type) switch
                {
                    ("Projection", ResourceBindingType.ConstantBuffer) => BindProjectionUniforms,
                    ("ProjectionViews", ResourceBindingType.BufferSrv) => BindProjectionViews,
                    ("ProjectionDepth", ResourceBindingType.TextureSrv) => BindProjectionDepth,
                    ("ProjectionReceivers", ResourceBindingType.BufferSrv) => BindProjectionReceivers,
                    ("ProjectionAdditionalReceivers", ResourceBindingType.BufferSrv) => BindProjectionAdditionalReceivers,
                    ("ProjectionVisibility", ResourceBindingType.TextureUav) => BindProjectionVisibilityOutput,
                    ("ProjectionAdditionalVisibility", ResourceBindingType.TextureUav) => BindProjectionAdditionalVisibilityOutput,
                    _ => null,
                };
                if (binder is null && !TryResolve(name, type, out binder) &&
                    !_lighting.TryResolve(name, type, out binder)) return null;
                binders.Add(binder!);
                return binders.Count - 1;
            });
        PassDefinition pass = edit.GetPass(_projectionPass);
        foreach (int resource in resources)
            binders[resource](ref edit, ref pass, PipelineSync.ComputeShading);
        edit.OrderAfter(_projectionPass, _rasterCompletePass);
        edit.OrderAfter(_projectionPass, _prefilterInitialize);
        if (_prefilterPasses.Length != 0) edit.OrderAfter(_projectionPass, _prefilterPasses[^1]);
        edit.OrderAfter(_frameCompletePass, _projectionPass);
    }

    private void StageProjection(ref RenderGraphFrame graph, in ClusterPipelineOutputs geometry, uint lightCount)
    {
        uint lightCapacity = Math.Max(lightCount, 1u);
        uint layers = checked(lightCapacity * geometry.ViewCount);
        uint width = checked((uint)geometry.Width), height = checked((uint)geometry.Height);
        GraphTextureId visibility = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D,
            width, height, 1, 1, layers, 1, Format.R32Float, TextureUsages.Sampled | TextureUsages.Storage,
            label: "Virtual shadow receiver light visibility"));
        GraphTextureId additional = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D,
            _frameAdditionalReceiverCount == 0u ? 1u : width,
            _frameAdditionalReceiverCount == 0u ? 1u : height, 1, 1,
            Math.Max(checked(layers * _frameAdditionalReceiverCount), 1u), 1, Format.R32Float,
            TextureUsages.Sampled | TextureUsages.Storage, label: "Virtual shadow additional receiver light visibility"));
        graph.SetTextureInput(_projectedVisibility, visibility);
        graph.SetTextureInput(_additionalProjectedVisibility, additional);
        graph.SetBufferInput(_projectionViews, new(geometry.Geometry.PackedViews, BufferRange.Whole));
        var uniforms = new ProjectionUniforms
        {
            Width = width, Height = height, ViewCount = geometry.ViewCount, LightCapacity = lightCapacity,
            AdditionalReceiverLayerCount = _frameAdditionalReceiverCount,
        };
        _uniformData.Write(ref graph, _projectionUniforms, in uniforms);
        graph.SetPassData(_projectionPass, new VirtualShadowDispatchPassData(new(
            DivideRoundUp(width, 8u), DivideRoundUp(height, 8u),
            checked(layers * (1u + _frameAdditionalReceiverCount)))));
    }

    private void BindProjectionUniforms(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(_projectionUniforms, sync);

    private void BindProjectionViews(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_projectionViews, structureStride: ClusterRenderContext.ViewSizeInBytes), sync);

    private void BindProjectionDepth(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateTextureSrv(_receiverDepth, format: Format.D32Float,
            dimension: TextureViewDimension.Texture2DArray), sync);

    private void BindProjectionReceivers(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_surfaceReceivers, structureStride: 32), sync);

    private void BindProjectionAdditionalReceivers(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_additionalReceivers, structureStride: 32), sync);

    private void BindProjectionVisibilityOutput(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateTextureUav(_projectedVisibility, format: Format.R32Float,
            dimension: TextureViewDimension.Texture2DArray), sync, GraphAccessMode.Write, WriteCoverage.Complete);

    private void BindProjectionAdditionalVisibilityOutput(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateTextureUav(_additionalProjectedVisibility, format: Format.R32Float,
            dimension: TextureViewDimension.Texture2DArray), sync, GraphAccessMode.Write, WriteCoverage.Complete);

    private void BindProjectedVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateTextureSrv(_projectedVisibility, format: Format.R32Float,
            dimension: TextureViewDimension.Texture2DArray), sync);

    private void BindAdditionalProjectedVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateTextureSrv(_additionalProjectedVisibility, format: Format.R32Float,
            dimension: TextureViewDimension.Texture2DArray), sync);
}
