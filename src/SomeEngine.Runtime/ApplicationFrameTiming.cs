using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

/// <summary>One presented frame, delivered after its GPU timestamps and geometry counters complete.</summary>
/// <param name="CpuMs">From the earlier of frame entry and its asynchronous game-update scheduling through Present.</param>
/// <param name="GameMs">Game callbacks, animation and interface updates before extraction.</param>
public sealed record ApplicationFrameTiming(
    int Frame, double CpuMs, double GameMs, double AnimationMs, double ExtractionMs,
    double PrepareMs, double ReconcileMs, double ExecuteMs, RenderGraphGpuTimes Gpu, ClusterFrameMetrics Geometry);

internal sealed class ApplicationFrameMeasurements(Action<ApplicationFrameTiming> receive)
{
    private readonly Dictionary<ulong, FrameTimes> _frames = [];

    internal void RecordCpu(ulong identity, int frame, double cpuMs, double gameMs, double animationMs, double extractionMs,
        double prepareMs, double reconcileMs, double executeMs)
    {
        FrameTimes times = Get(identity);
        times.Cpu = new(frame, cpuMs, gameMs, animationMs, extractionMs, prepareMs, reconcileMs, executeMs);
        times.HasCpu = true;
        Deliver(identity, times);
    }

    internal void ReceiveGpu(RenderGraphGpuTimes gpu)
    {
        FrameTimes times = Get(gpu.FrameIdentity);
        times.Gpu = gpu;
        Deliver(gpu.FrameIdentity, times);
    }

    internal void ReceiveGeometry(ClusterFrameMetrics geometry)
    {
        FrameTimes times = Get(geometry.FrameIdentity);
        times.Geometry = geometry;
        Deliver(geometry.FrameIdentity, times);
    }

    private FrameTimes Get(ulong identity)
    {
        if (!_frames.TryGetValue(identity, out FrameTimes? times)) _frames.Add(identity, times = new());
        return times;
    }

    private void Deliver(ulong identity, FrameTimes times)
    {
        if (!times.HasCpu || times.Gpu is null || times.Geometry is null) return;
        _frames.Remove(identity);
        CpuFrameTime cpu = times.Cpu;
        receive(new(cpu.Frame, cpu.CpuMs, cpu.GameMs, cpu.AnimationMs, cpu.ExtractionMs,
            cpu.PrepareMs, cpu.ReconcileMs, cpu.ExecuteMs, times.Gpu, times.Geometry.Value));
    }

    private sealed class FrameTimes
    {
        internal CpuFrameTime Cpu;
        internal bool HasCpu;
        internal RenderGraphGpuTimes? Gpu;
        internal ClusterFrameMetrics? Geometry;
    }

    private readonly record struct CpuFrameTime(int Frame, double CpuMs, double GameMs, double AnimationMs, double ExtractionMs,
        double PrepareMs, double ReconcileMs, double ExecuteMs);
}
