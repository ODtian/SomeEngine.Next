using SomeEngine.RenderGraph;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

/// <summary>One presented frame, delivered after its GPU timestamps and geometry counters complete.</summary>
/// <param name="CpuMs">From the earlier of frame entry and its asynchronous game-update scheduling through Present.</param>
/// <param name="GameMs">Game callbacks, animation and interface updates before extraction.</param>
public sealed record ApplicationFrameTiming(
    int Frame, double CpuMs, double GameMs, double AnimationMs, double ExtractionMs,
    double PrepareMs, double ReconcileMs, double ExecuteMs, RenderGraphGpuTimes Gpu, ClusterFrameMetrics Geometry)
{
    public double SimulationSeconds { get; init; }
    /// <summary>Whether the scene uses the asynchronous Game-update scheduler.</summary>
    public bool AsynchronousGameUpdates { get; init; }
}

internal sealed class ApplicationFrameMeasurements(Action<ApplicationFrameTiming> receive)
{
    private readonly Dictionary<ulong, FrameTimes> _frames = [];

    internal void RecordCpu(ulong identity, int frame, double simulationSeconds, bool asynchronousGameUpdates, double cpuMs, double gameMs, double animationMs, double extractionMs,
        double prepareMs, double reconcileMs, double executeMs)
    {
        FrameTimes times = GetFrame(identity);
        times.Cpu = new(frame, simulationSeconds, asynchronousGameUpdates, cpuMs, gameMs, animationMs, extractionMs, prepareMs, reconcileMs, executeMs);
        Publish(identity, times);
    }

    internal void ReceiveGpu(RenderGraphGpuTimes gpu)
    {
        FrameTimes times = GetFrame(gpu.FrameIdentity);
        times.Gpu = gpu;
        Publish(gpu.FrameIdentity, times);
    }

    internal void ReceiveGeometry(ClusterFrameMetrics geometry)
    {
        FrameTimes times = GetFrame(geometry.FrameIdentity);
        times.Geometry = geometry;
        Publish(geometry.FrameIdentity, times);
    }

    private FrameTimes GetFrame(ulong identity)
    {
        if (!_frames.TryGetValue(identity, out FrameTimes? times))
            _frames.Add(identity, times = new());
        return times;
    }

    private void Publish(ulong identity, FrameTimes times)
    {
        if (times.Cpu is not { } cpu || times.Gpu is not { } gpu || times.Geometry is not { } geometry) return;
        _frames.Remove(identity);
        receive(new(cpu.Frame, cpu.CpuMs, cpu.GameMs, cpu.AnimationMs, cpu.ExtractionMs,
            cpu.PrepareMs, cpu.ReconcileMs, cpu.ExecuteMs, gpu, geometry)
        {
            SimulationSeconds = cpu.SimulationSeconds, AsynchronousGameUpdates = cpu.AsynchronousGameUpdates,
        });
    }

    private sealed class FrameTimes
    {
        internal CpuFrameTime? Cpu;
        internal RenderGraphGpuTimes? Gpu;
        internal ClusterFrameMetrics? Geometry;
    }

    private readonly record struct CpuFrameTime(int Frame, double SimulationSeconds, bool AsynchronousGameUpdates, double CpuMs, double GameMs, double AnimationMs, double ExtractionMs,
        double PrepareMs, double ReconcileMs, double ExecuteMs);
}
