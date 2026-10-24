using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

/// <summary>One presented frame, delivered after its GPU timestamps complete.</summary>
/// <param name="CpuMs">From the earlier of frame entry and its asynchronous game-update scheduling through Present.</param>
/// <param name="GameMs">Game callbacks, animation and interface updates before extraction.</param>
public sealed record ApplicationFrameTiming(
    int Frame, double CpuMs, double GameMs, double AnimationMs, double ExtractionMs,
    double PrepareMs, double ReconcileMs, double ExecuteMs, RenderGraphGpuTimes Gpu);

internal sealed class ApplicationFrameMeasurements(Action<ApplicationFrameTiming> receive)
{
    private readonly Dictionary<ulong, FrameTimes> _frames = [];

    internal void RecordCpu(ulong identity, int frame, double cpuMs, double gameMs, double animationMs, double extractionMs,
        double prepareMs, double reconcileMs, double executeMs)
    {
        FrameTimes times = GetFrame(identity);
        times.Cpu = new(frame, cpuMs, gameMs, animationMs, extractionMs, prepareMs, reconcileMs, executeMs);
        Publish(identity, times);
    }

    internal void ReceiveGpu(RenderGraphGpuTimes gpu)
    {
        FrameTimes times = GetFrame(gpu.FrameIdentity);
        times.Gpu = gpu;
        Publish(gpu.FrameIdentity, times);
    }

    private FrameTimes GetFrame(ulong identity)
    {
        if (!_frames.TryGetValue(identity, out FrameTimes? times))
            _frames.Add(identity, times = new());
        return times;
    }

    private void Publish(ulong identity, FrameTimes times)
    {
        if (times.Cpu is not { } cpu || times.Gpu is not { } gpu) return;
        _frames.Remove(identity);
        receive(new(cpu.Frame, cpu.CpuMs, cpu.GameMs, cpu.AnimationMs, cpu.ExtractionMs,
            cpu.PrepareMs, cpu.ReconcileMs, cpu.ExecuteMs, gpu));
    }

    private sealed class FrameTimes
    {
        internal CpuFrameTime? Cpu;
        internal RenderGraphGpuTimes? Gpu;
    }

    private readonly record struct CpuFrameTime(int Frame, double CpuMs, double GameMs, double AnimationMs, double ExtractionMs,
        double PrepareMs, double ReconcileMs, double ExecuteMs);
}
