using SomeEngine.Render.Lighting;
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
    private GraphBufferId _unusedProjectionData;

    private sealed class LightProjection
    {
        internal LinkedComputePipeline Pipeline = null!;
        internal GraphPassId Pass;
        internal GraphBufferInput Uniforms = null!;
        internal GraphBufferInput Visibility = null!;
        internal GraphBufferInput AdditionalVisibility = null!;
        internal uint BytesPerPixel;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    private struct ProjectionUniforms
    {
        public uint Width, Height, ViewCount, LightLayerCount;
        public uint AdditionalReceiverLayerCount, DispatchDepth, LightLayerRowCount, Pad2;
    }

    private void InstallProjection(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders, GraphRegionId region)
    {
        _projectionViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _unusedProjectionData = edit.CreateBuffer(new BufferDesc(16u,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Inactive shadow projection binding"));
        // Forward shading reads completed pages directly and skips the projection passes.
        edit.OrderAfter(_frameCompletePass, _rasterCompletePass);
        edit.OrderAfter(_frameCompletePass, _prefilterInitialize);
        _directionalProjection = InstallLightProjection(ref edit, shaders.ProjectDirectionalLightsShader,
            shaders.ProjectDirectionalLights, "Project directional virtual shadows", 4u, region);
        _localProjection = InstallLightProjection(ref edit, shaders.ProjectLocalLightsShader,
            shaders.ProjectLocalLights, "Project local virtual shadows", 16u, region);
    }

    private LightProjection InstallLightProjection(ref RenderGraphEdit edit, Shader? shader,
        SlangShaderSharp.EntryPointReflection entry, string label, uint bytesPerPixel, GraphRegionId region)
    {
        var projection = new LightProjection
        {
            Pipeline = LinkedComputePipeline.Create(_backend, _device, shader, entry, label),
            Uniforms = edit.CreateBufferInput(BufferUsages.Constant),
            Visibility = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite),
            AdditionalVisibility = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite),
            BytesPerPixel = bytesPerPixel,
        };
        try
        {
            projection.Pass = AddComputePass(ref edit, label, projection.Pipeline,
                new VirtualShadowDispatchPassData(default), region);
            var binders = new List<GraphResourceBinder>();
            int[] resources = ShaderResourceLayout.Resolve<int>(projection.Pipeline.ParameterLayout,
                (name, type) =>
                {
                    GraphResourceBinder? binder = (name, type) switch
                    {
                        ("Projection", ResourceBindingType.ConstantBuffer) =>
                            (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                                _ = pass.Bind(projection.Uniforms, sync),
                        ("ProjectionVisibility", ResourceBindingType.BufferUav) =>
                            (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                                // Every value is initialized within this pass before its pixel updates it.
                                _ = pass.Bind(graph.CreateBufferUav(projection.Visibility, structureStride: projection.BytesPerPixel),
                                    sync, GraphAccessMode.Write, WriteCoverage.Complete),
                        ("ProjectionAdditionalVisibility", ResourceBindingType.BufferUav) =>
                            (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                                _ = pass.Bind(graph.CreateBufferUav(projection.AdditionalVisibility, structureStride: projection.BytesPerPixel),
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
        bool enabled = _geometry.InstalledShadingPath == ClusterShadingPath.Deferred;
        graph.SetPassEnabled(_directionalProjection.Pass, enabled);
        graph.SetPassEnabled(_localProjection.Pass, enabled);
        graph.SetBufferInput(_projectionViews, new(geometry.Geometry.PackedViews, BufferRange.Whole));
        if (!enabled)
        {
            // Persistent graph views still need a current input while their passes are disabled.
            // No active shader reads this shared descriptor placeholder.
            GraphBufferSlice unused = new(_unusedProjectionData, BufferRange.Whole);
            graph.SetBufferInput(_directionalProjection.Visibility, unused);
            graph.SetBufferInput(_directionalProjection.AdditionalVisibility, unused);
            graph.SetBufferInput(_localProjection.Visibility, unused);
            graph.SetBufferInput(_localProjection.AdditionalVisibility, unused);
            ProjectionUniforms uniforms = default;
            _uniformData.Write(ref graph, _directionalProjection.Uniforms, in uniforms);
            _uniformData.Write(ref graph, _localProjection.Uniforms, in uniforms);
            return;
        }
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
        uint resultLayers = projection.BytesPerPixel == 16u ? DivideRoundUp(lightLayers, 32u) : lightLayers;
        uint dispatchLightLayers = projection.BytesPerPixel == 16u ? 1u : lightLayers;
        ulong values = checked((ulong)DivideRoundUp(width, 8u) * DivideRoundUp(height, 8u) * 64u * views * resultLayers);
        GraphBufferId visibility = graph.CreateBuffer(new BufferDesc(checked(values * projection.BytesPerPixel),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Virtual shadow receiver light visibility"));
        GraphBufferId additional = graph.CreateBuffer(new BufferDesc(Math.Max(checked(values * receiverLayers * projection.BytesPerPixel), projection.BytesPerPixel),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Virtual shadow additional receiver light visibility"));
        graph.SetBufferInput(projection.Visibility, new(visibility, BufferRange.Whole));
        graph.SetBufferInput(projection.AdditionalVisibility, new(additional, BufferRange.Whole));
        uint layers = checked(views * dispatchLightLayers * (1u + receiverLayers));
        uint dispatchDepth = Math.Min(layers, 65535u);
        uint layerRows = DivideRoundUp(layers, dispatchDepth);
        var uniforms = new ProjectionUniforms
        {
            Width = width, Height = height, ViewCount = views, LightLayerCount = lightLayers,
            AdditionalReceiverLayerCount = receiverLayers, DispatchDepth = dispatchDepth,
            LightLayerRowCount = layerRows,
        };
        _uniformData.Write(ref graph, projection.Uniforms, in uniforms);
        graph.SetPassData(projection.Pass, new VirtualShadowDispatchPassData(new(
            DivideRoundUp(width, 8u), checked(DivideRoundUp(height, 8u) * layerRows), dispatchDepth)));
    }

    private void BindProjectionViews(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_projectionViews, structureStride: ClusterRenderContext.ViewSizeInBytes), sync);

    private void BindProjectionDepth(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateTextureSrv(_receiverDepth, format: Format.D32Float,
            dimension: TextureViewDimension.Texture2DArray), sync);

    private void BindProjectionReceivers(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_surfaceReceivers, structureStride: SurfaceReceiver.SizeInBytes), sync);

    private void BindProjectionAdditionalReceivers(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_additionalReceivers, structureStride: SurfaceReceiver.SizeInBytes), sync);

    private void BindProjectedDirectionalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_directionalProjection.Visibility, structureStride: 4), sync);

    private void BindProjectedLocalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_localProjection.Visibility, structureStride: 16), sync);

    private void BindAdditionalProjectedDirectionalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_directionalProjection.AdditionalVisibility, structureStride: 4), sync);

    private void BindAdditionalProjectedLocalVisibility(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(edit.CreateBufferSrv(_localProjection.AdditionalVisibility, structureStride: 16), sync);
}
