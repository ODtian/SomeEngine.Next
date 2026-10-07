using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private LightProjection _directionalProjection = null!;
    private LightProjection _localProjection = null!;
    private GraphBufferInput _projectionViews = null!;

    private sealed class LightProjection
    {
        internal LinkedComputePipeline Pipeline = null!;
        internal GraphPassId Pass;
        internal GraphBufferInput Uniforms = null!;
        internal GraphBufferInput Visibility = null!;
        internal GraphBufferInput AdditionalVisibility = null!;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    private struct ProjectionUniforms
    {
        public uint Width, Height, ViewCount, LightLayerCount;
        public uint AdditionalReceiverLayerCount, DispatchWidth, Pad1, Pad2;
    }

    private void InstallProjection(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders, GraphRegionId region)
    {
        _projectionViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _directionalProjection = InstallLightProjection(ref edit, shaders.ProjectDirectionalLightsShader,
            shaders.ProjectDirectionalLights, "Project directional virtual shadows", region);
        _localProjection = InstallLightProjection(ref edit, shaders.ProjectLocalLightsShader,
            shaders.ProjectLocalLights, "Project local virtual shadows", region);
    }

    private LightProjection InstallLightProjection(ref RenderGraphEdit edit, Shader? shader,
        SlangShaderSharp.EntryPointReflection entry, string label, GraphRegionId region)
    {
        var projection = new LightProjection
        {
            Pipeline = LinkedComputePipeline.Create(_backend, _device, shader, entry, label),
            Uniforms = edit.CreateBufferInput(BufferUsages.Constant),
            Visibility = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite),
            AdditionalVisibility = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite),
        };
        try
        {
            projection.Pass = AddComputePass(ref edit, label, projection.Pipeline,
                new VirtualShadowDispatchPassData(default), region);
            var binders = new List<GraphResourceBinder>();
            int[] resources = FeatureResourceLayout.Resolve<int>(projection.Pipeline.ParameterLayout,
                (name, type) =>
                {
                    GraphResourceBinder? binder = (name, type) switch
                    {
                        ("Projection", ResourceBindingType.ConstantBuffer) =>
                            (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                                _ = pass.Bind(projection.Uniforms, sync),
                        ("ProjectionVisibility", ResourceBindingType.BufferUav) =>
                            (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                                _ = pass.Bind(graph.CreateBufferUav(projection.Visibility, structureStride: 4),
                                    sync, GraphAccessMode.Write, WriteCoverage.Complete),
                        ("ProjectionAdditionalVisibility", ResourceBindingType.BufferUav) =>
                            (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                                _ = pass.Bind(graph.CreateBufferUav(projection.AdditionalVisibility, structureStride: 4),
                                    sync, GraphAccessMode.Write, WriteCoverage.Complete),
                        ("ProjectionViews", ResourceBindingType.BufferSrv) => BindProjectionViews,
                        ("ProjectionDepth", ResourceBindingType.TextureSrv) => BindProjectionDepth,
                        ("ProjectionReceivers", ResourceBindingType.BufferSrv) => BindProjectionReceivers,
                        ("ProjectionAdditionalReceivers", ResourceBindingType.BufferSrv) => BindProjectionAdditionalReceivers,
                        _ => null,
                    };
                    if (binder is null && !TryResolve(name, type, out binder) &&
                        !_lighting.TryResolve(name, type, out binder)) return null;
                    binders.Add(binder!);
                    return binders.Count - 1;
                });
            PassDefinition pass = edit.GetPass(projection.Pass);
            foreach (int resource in resources)
                binders[resource](ref edit, ref pass, PipelineSync.ComputeShading);
            edit.OrderAfter(projection.Pass, _rasterCompletePass);
            edit.OrderAfter(projection.Pass, _prefilterInitialize);
            if (_prefilterPasses.Length != 0) edit.OrderAfter(projection.Pass, _prefilterPasses[^1]);
            edit.OrderAfter(_frameCompletePass, projection.Pass);
            return projection;
        }
        catch { projection.Pipeline.Dispose(); throw; }
    }

    private void StageProjection(ref RenderGraphFrame graph, in ClusterPipelineOutputs geometry,
        uint directionalLightCount, uint localLightCount)
    {
        graph.SetBufferInput(_projectionViews, new(geometry.Geometry.PackedViews, BufferRange.Whole));
        StageLightProjection(ref graph, _directionalProjection, in geometry, directionalLightCount);
        StageLightProjection(ref graph, _localProjection, in geometry, localLightCount);
    }

    private void StageLightProjection(ref RenderGraphFrame graph, LightProjection projection,
        in ClusterPipelineOutputs geometry, uint lightCount)
    {
        uint lightLayers = Math.Max(lightCount, 1u);
        uint width = lightCount == 0u ? 1u : checked((uint)geometry.Width);
        uint height = lightCount == 0u ? 1u : checked((uint)geometry.Height);
        uint views = lightCount == 0u ? 1u : geometry.ViewCount;
        uint receiverLayers = lightCount == 0u ? 0u : _frameAdditionalReceiverCount;
        ulong values = checked((ulong)width * height * views * lightLayers);
        GraphBufferId visibility = graph.CreateBuffer(new BufferDesc(checked(values * 4u),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Virtual shadow receiver light visibility"));
        GraphBufferId additional = graph.CreateBuffer(new BufferDesc(Math.Max(checked(values * receiverLayers * 4u), 4u),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Virtual shadow additional receiver light visibility"));
        graph.SetBufferInput(projection.Visibility, new(visibility, BufferRange.Whole));
        graph.SetBufferInput(projection.AdditionalVisibility, new(additional, BufferRange.Whole));
        uint groups = checked(DivideRoundUp(width, 8u) * DivideRoundUp(height, 8u) *
            views * lightLayers * (1u + receiverLayers));
        uint dispatchWidth = Math.Min(groups, 65535u);
        var uniforms = new ProjectionUniforms
        {
            Width = width, Height = height, ViewCount = views, LightLayerCount = lightLayers,
            AdditionalReceiverLayerCount = receiverLayers, DispatchWidth = dispatchWidth,
        };
        _uniformData.Write(ref graph, projection.Uniforms, in uniforms);
        graph.SetPassData(projection.Pass, new VirtualShadowDispatchPassData(new(
            dispatchWidth, DivideRoundUp(groups, dispatchWidth), 1u)));
    }

    private void BindProjectionViews(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_projectionViews, structureStride: ClusterRenderContext.ViewSizeInBytes), sync);

    private void BindProjectionDepth(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateTextureSrv(_receiverDepth, format: Format.D32Float,
            dimension: TextureViewDimension.Texture2DArray), sync);

    private void BindProjectionReceivers(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_surfaceReceivers, structureStride: 32), sync);

    private void BindProjectionAdditionalReceivers(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_additionalReceivers, structureStride: 32), sync);

    private void BindProjectedDirectionalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_directionalProjection.Visibility, structureStride: 4), sync);

    private void BindProjectedLocalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_localProjection.Visibility, structureStride: 4), sync);

    private void BindAdditionalProjectedDirectionalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_directionalProjection.AdditionalVisibility, structureStride: 4), sync);

    private void BindAdditionalProjectedLocalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_localProjection.AdditionalVisibility, structureStride: 4), sync);
}
