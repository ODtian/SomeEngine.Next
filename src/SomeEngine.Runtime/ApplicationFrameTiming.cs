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
    private readonly Dictionary<ulong, CpuFrameTime> _cpu = [];

    internal void RecordCpu(ulong identity, int frame, double cpuMs, double gameMs, double animationMs, double extractionMs,
        double prepareMs, double reconcileMs, double executeMs) =>
        _cpu.Add(identity, new(frame, cpuMs, gameMs, animationMs, extractionMs, prepareMs, reconcileMs, executeMs));

    internal void ReceiveGpu(RenderGraphGpuTimes gpu)
    {
        CpuFrameTime cpu = _cpu[gpu.FrameIdentity];
        _cpu.Remove(gpu.FrameIdentity);
        receive(new(cpu.Frame, cpu.CpuMs, cpu.GameMs, cpu.AnimationMs, cpu.ExtractionMs,
            cpu.PrepareMs, cpu.ReconcileMs, cpu.ExecuteMs, gpu));
    }

    private readonly record struct CpuFrameTime(int Frame, double CpuMs, double GameMs, double AnimationMs, double ExtractionMs,
        double PrepareMs, double ReconcileMs, double ExecuteMs);
}
