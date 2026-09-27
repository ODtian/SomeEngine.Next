using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.GeometryExport;

public readonly record struct StreamOutputFeatureOutput(
    GraphBufferId Clusters,
    GraphBufferId Count,
    uint Capacity)
{
    public bool IsValid => Clusters != default && Count != default && Capacity != 0;
}

/// <summary>
/// Independently exports the bounded active Cluster geometry worklist for stream-output consumers.
/// Its resources and compute pass are feature-owned and it has no Cluster shader operation role.
/// </summary>
public sealed class StreamOutputFeature : IDisposable
{
    private readonly VisibleGeometryExportPass _implementation;

    public StreamOutputFeature(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        in ClusterPipelineOutputs geometry,
        Shader shader,
        string entryPoint)
    {
        _implementation = new VisibleGeometryExportPass(
            backend,
            device,
            graph,
            geometry,
            shader,
            entryPoint,
            outputStride: 12,
            readsPageHeap: false,
            label: "Stream output geometry");
        Output = new StreamOutputFeatureOutput(
            _implementation.Output,
            _implementation.Count,
            geometry.Geometry.VisibleClusterCapacity);
    }

    public StreamOutputFeatureOutput Output { get; }

    public void Record(ref RenderGraphFrame graph, bool enabled = true) =>
        _implementation.Record(ref graph, enabled);

    public void Dispose() => _implementation.Dispose();
}

public readonly record struct RayTracingGeometryFeatureOutput(
    GraphBufferId ClusterRecords,
    GraphBufferId Count,
    uint Capacity,
    uint RecordStride)
{
    public bool IsValid =>
        ClusterRecords != default && Count != default && Capacity != 0 && RecordStride != 0;
}

/// <summary>
/// Independently prepares bounded ray-tracing cluster records from explicit geometry capability.
/// Acceleration-structure policy remains outside Cluster and can consume this feature output.
/// </summary>
public sealed class RayTracingGeometryFeature : IDisposable
{
    public const uint RecordStride = 28;
    private readonly VisibleGeometryExportPass _implementation;

    public RayTracingGeometryFeature(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        in ClusterPipelineOutputs geometry,
        Shader shader,
        string entryPoint)
    {
        _implementation = new VisibleGeometryExportPass(
            backend,
            device,
            graph,
            geometry,
            shader,
            entryPoint,
            outputStride: RecordStride,
            readsPageHeap: true,
            label: "Ray tracing geometry");
        Output = new RayTracingGeometryFeatureOutput(
            _implementation.Output,
            _implementation.Count,
            geometry.Geometry.VisibleClusterCapacity,
            RecordStride);
    }

    public RayTracingGeometryFeatureOutput Output { get; }

    public void Record(ref RenderGraphFrame graph, bool enabled = true) =>
        _implementation.Record(ref graph, enabled);

    public void Dispose() => _implementation.Dispose();
}

internal sealed class VisibleGeometryExportPass : IDisposable
{
    private readonly LinkedComputePipeline _pipeline;
    private readonly ClusterGeometryCapabilities _geometry;
    private readonly ExportResource[] _resourceOrder;
    private readonly GraphPassId _pass;
    private readonly bool _readsPageHeap;
    private readonly GraphBufferInput _uniforms;
    private bool _disposed;

    internal VisibleGeometryExportPass(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        in ClusterPipelineOutputs geometry,
        Shader shader,
        string entryPoint,
        uint outputStride,
        bool readsPageHeap,
        string label)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        if (!geometry.IsValid)
            throw new ArgumentException("Geometry export requires valid Cluster capability.", nameof(geometry));
        ArgumentOutOfRangeException.ThrowIfZero(outputStride);
        _geometry = geometry.Geometry;
        _readsPageHeap = readsPageHeap;
        LinkedComputePipeline? pipeline = null;
        try
        {
            pipeline = LinkedComputePipeline.Create(backend, device, shader, entryPoint, label);
            _resourceOrder = FeatureResourceLayout.Resolve<ExportResource>(
                pipeline.ParameterLayout,
                ResolveResource);
            if (_resourceOrder.Contains(ExportResource.PageHeap) != readsPageHeap)
            {
                throw new InvalidDataException(
                    $"Geometry export shader '{entryPoint}' page-heap reflection does not match its feature contract.");
            }
            using RenderGraphEdit edit = graph.BeginEdit();
            Output = edit.CreatePersistentBuffer(new BufferDesc(
                checked((ulong)_geometry.VisibleClusterCapacity * outputStride),
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource,
                $"{label} records"));
            Count = edit.CreatePersistentBuffer(new BufferDesc(
                sizeof(uint),
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource,
                $"{label} count"));
            _pass = edit.AddComputeFramePass(
                label,
                PassQueueSelection.AnyOfType(QueueType.Compute),
                new ExportPassData(default),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                pipeline.Pipeline,
                pipeline.ParameterLayout,
                default,
                static (ref PassDefinition _, ref ExportPassData _) => { },
                ExportPassData.Record);
            edit.OrderAfter(_pass, _geometry.CompletePass);
            _uniforms = edit.CreateBufferInput(BufferUsages.Constant);
            PassDefinition pass = edit.GetPass(_pass);
            foreach (ExportResource resource in _resourceOrder)
            {
                switch (resource)
                {
                    case ExportResource.Uniforms:
                        _ = pass.Bind(
                            _uniforms,
                            PipelineSync.ComputeShading);
                        break;
                    case ExportResource.PhaseOneCounts:
                        _ = pass.Bind(
                            edit.CreateBufferSrv(_geometry.PhaseOneCounts, label: "Geometry phase-one counts"),
                            PipelineSync.ComputeShading);
                        break;
                    case ExportResource.PhaseTwoCounts:
                        _ = pass.Bind(
                            edit.CreateBufferSrv(_geometry.PhaseTwoCounts, label: "Geometry phase-two counts"),
                            PipelineSync.ComputeShading);
                        break;
                    case ExportResource.VisibleClusters:
                        _ = pass.Bind(
                            edit.CreateBufferSrv(
                                _geometry.VisibleClusters,
                                structureStride: 12,
                                label: "Visible geometry clusters"),
                            PipelineSync.ComputeShading,
                            BufferContentScope.CountBoundedPrefix);
                        break;
                    case ExportResource.PageHeap:
                        if (!_readsPageHeap)
                            throw new InvalidOperationException("This geometry export cannot read the page heap.");
                        _geometry.BindPageHeap(
                            edit,
                            ref pass,
                            PipelineSync.ComputeShading,
                            "Ray tracing page heap");
                        break;
                    case ExportResource.Output:
                        _ = pass.Bind(
                            edit.CreateBufferUav(Output, label: "Geometry export records"),
                            PipelineSync.ComputeShading,
                            GraphAccessMode.Write,
                            WriteCoverage.Partial);
                        break;
                    case ExportResource.Count:
                        _ = pass.Bind(
                            edit.CreateBufferUav(Count, label: "Geometry export count"),
                            PipelineSync.ComputeShading,
                            GraphAccessMode.Write,
                            WriteCoverage.Complete);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown geometry export resource key.");
                }
            }
            edit.Commit();
            _pipeline = pipeline;
            pipeline = null;
        }
        catch
        {
            pipeline?.Dispose();
            throw;
        }
    }

    internal GraphBufferId Output { get; }
    internal GraphBufferId Count { get; }

    internal void Record(ref RenderGraphFrame graph, bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var uniforms = new ExportUniforms
        {
            Capacity = _geometry.VisibleClusterCapacity,
            Enabled = enabled ? 1u : 0u,
        };
        GraphBufferSlice uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Geometry export uniforms");
        graph.SetBufferInput(_uniforms, uniformBuffer);
        if (_readsPageHeap)
        {
            PassDefinition pass = graph.GetPass(_pass);
            _geometry.ReadPageHeap(ref pass, PipelineSync.ComputeShading);
        }
        graph.SetPassData(
            _pass,
            new ExportPassData(new DispatchArguments(
                DivideRoundUp(_geometry.VisibleClusterCapacity, 64),
                1,
                1)));
        graph.SetPassEnabled(_pass, true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        _disposed = true;
    }

    private static ExportResource? ResolveResource(string name, ResourceBindingType type) =>
        (name, type) switch
        {
            ("ExportUniforms", ResourceBindingType.ConstantBuffer) => ExportResource.Uniforms,
            ("PhaseOneCounts", ResourceBindingType.BufferSrv) => ExportResource.PhaseOneCounts,
            ("PhaseTwoCounts", ResourceBindingType.BufferSrv) => ExportResource.PhaseTwoCounts,
            ("VisibleClusters", ResourceBindingType.BufferSrv) => ExportResource.VisibleClusters,
            ("PageHeap", ResourceBindingType.BufferSrv) => ExportResource.PageHeap,
            ("StreamOutputClusters", ResourceBindingType.BufferUav) => ExportResource.Output,
            ("RayTracingClusters", ResourceBindingType.BufferUav) => ExportResource.Output,
            ("StreamOutputCount", ResourceBindingType.BufferUav) => ExportResource.Count,
            ("RayTracingClusterCount", ResourceBindingType.BufferUav) => ExportResource.Count,
            _ => null,
        };

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);

    private enum ExportResource : byte
    {
        Uniforms,
        PhaseOneCounts,
        PhaseTwoCounts,
        VisibleClusters,
        PageHeap,
        Output,
        Count,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    private struct ExportUniforms
    {
        internal uint Capacity;
        internal uint Enabled;
    }

    private readonly record struct ExportPassData(DispatchArguments Dispatch)
    {
        internal static void Record(ref ComputePassCommandScope commands, in ExportPassData data) =>
            commands.Dispatch(data.Dispatch);
    }
}
