using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Motion;

public readonly record struct MotionFeatureOutput(
    GraphTextureId MotionVectors,
    int Width,
    int Height,
    uint ViewCapacity)
{
    public bool IsValid =>
        MotionVectors != default && Width > 0 && Height > 0 && ViewCapacity != 0;
}

/// <summary>
/// Independently reconstructs per-pixel motion from explicit visibility and geometry capability.
/// It owns its output and retained compute pass and never participates in Cluster operation roles.
/// </summary>
public sealed class MotionFeature : IDisposable
{
    private readonly LinkedComputePipeline _pipeline;
    private readonly ClusterPipelineOutputs _geometry;
    private readonly GraphPassId _pass;
    private bool _disposed;

    public MotionFeature(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        in ClusterPipelineOutputs geometry,
        Shader shader,
        string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        if (!geometry.IsValid)
            throw new ArgumentException("Motion requires valid Cluster geometry outputs.", nameof(geometry));
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Motion and RenderGraph must use the same Device.", nameof(graph));

        _geometry = geometry;
        LinkedComputePipeline? pipeline = null;
        try
        {
            pipeline = LinkedComputePipeline.Create(
                backend,
                device,
                shader,
                entryPoint,
                "Build motion vectors");
            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                GraphTextureId output = edit.CreateTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    checked((uint)geometry.Width),
                    checked((uint)geometry.Height),
                    1,
                    1,
                    geometry.ViewCapacity,
                    1,
                    Format.R16G16Float,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource,
                    label: "Motion vectors"));
                var data = new MotionPassData(default);
                _pass = edit.AddComputeFramePass(
                    "Build motion vectors",
                    PassQueueSelection.AnyOfType(QueueType.Compute),
                    data,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    pipeline.Pipeline,
                    pipeline.ParameterLayout,
                    default,
                    static (ref PassDefinition _, ref MotionPassData _) => { },
                    MotionPassData.Record);
                edit.Commit();
                Output = new MotionFeatureOutput(
                    output,
                    geometry.Width,
                    geometry.Height,
                    geometry.ViewCapacity);
            }
            finally
            {
                edit.Dispose();
            }
            _pipeline = pipeline;
            pipeline = null;
        }
        catch
        {
            pipeline?.Dispose();
            throw;
        }
    }

    public MotionFeatureOutput Output { get; }

    public void Record(ref RenderGraphFrame graph, bool enabled = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var uniforms = new MotionFeatureUniforms
        {
            ScreenWidth = checked((uint)Output.Width),
            ScreenHeight = checked((uint)Output.Height),
            ViewCapacity = Output.ViewCapacity,
            Enabled = enabled ? 1u : 0u,
        };
        GraphBufferId uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Motion feature uniforms");
        ClusterGeometryCapabilities geometry = _geometry.Geometry;
        PassDefinition pass = graph.GetPass(_pass);
        _ = pass.Bind(
            graph.CreateBufferSrv(geometry.InstanceData, label: "Motion instance data"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferCbv(
                geometry.InstanceProperties,
                geometry.InstancePropertiesRange,
                "Motion instance properties"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferCbv(uniformBuffer, label: "Motion uniforms"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                geometry.PackedViews,
                structureStride: 416,
                label: "Motion views"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _geometry.Visibility,
                format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Motion visibility"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                geometry.VisibleClusters,
                structureStride: 16,
                label: "Motion visible clusters"),
            PipelineSync.ComputeShading);
        geometry.BindPageHeap(
            ref graph,
            ref pass,
            PipelineSync.ComputeShading,
            "Motion page heap");
        _ = pass.Bind(
            graph.CreateBufferSrv(geometry.DeformCache, label: "Motion deform cache"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                geometry.CacheOffsets,
                structureStride: sizeof(uint),
                label: "Motion cache offsets"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureUav(
                Output.MotionVectors,
                format: Format.R16G16Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Motion output"),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete);
        graph.SetPassData(
            _pass,
            new MotionPassData(new DispatchArguments(
                DivideRoundUp(checked((uint)Output.Width), 8),
                DivideRoundUp(checked((uint)Output.Height), 8),
                Output.ViewCapacity)));
        graph.SetPassEnabled(_pass, true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        _disposed = true;
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    private struct MotionFeatureUniforms
    {
        internal uint ScreenWidth;
        internal uint ScreenHeight;
        internal uint ViewCapacity;
        internal uint Enabled;
    }

    private readonly record struct MotionPassData(DispatchArguments Dispatch)
    {
        internal static void Record(
            ref ComputePassCommandScope commands,
            in MotionPassData data) => commands.Dispatch(data.Dispatch);
    }
}
