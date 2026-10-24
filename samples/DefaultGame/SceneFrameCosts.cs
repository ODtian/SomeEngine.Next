using SomeEngine.Runtime;

namespace DefaultGame;

public sealed partial class SceneTestPlatform
{
    private readonly Dictionary<int, ApplicationFrameTiming> _completedFrames = [];

    private void RecordCompletedFrame(ApplicationFrameTiming frame)
    {
        Current.FrameMeasured?.Invoke(frame);
        if (IsMeasuring) _completedFrames.Add(frame.Frame, frame);
    }

    private SceneFrameCosts SummarizeCosts()
    {
        ApplicationFrameTiming[] frames = _frames.Select(frame => _completedFrames[frame.SimulationFrame]).ToArray();
        string[] stages = frames.SelectMany(frame => frame.Gpu.Passes)
            .Select(pass => pass.Group ?? pass.Label).Distinct().Order().ToArray();
        string[] queues = frames.SelectMany(frame => frame.Gpu.Passes)
            .Select(pass => $"{pass.QueueType}:{pass.QueueIndex}").Distinct().Order().ToArray();
        return new()
        {
            Frames = frames.Length,
            FrameRange = new(frames[0].Frame, frames[^1].Frame),
            SimulationSeconds = new(frames[0].SimulationSeconds, frames[^1].SimulationSeconds),
            AsynchronousGameUpdates = frames.All(frame => frame.AsynchronousGameUpdates),
            CpuMs = Distribution(frames.Select(frame => frame.CpuMs)),
            GameMs = Distribution(frames.Select(frame => frame.GameMs)),
            AnimationMs = Distribution(frames.Select(frame => frame.AnimationMs)),
            ExtractionMs = Distribution(frames.Select(frame => frame.ExtractionMs)),
            PrepareMs = Distribution(frames.Select(frame => frame.PrepareMs)),
            ReconcileMs = Distribution(frames.Select(frame => frame.ReconcileMs)),
            ExecuteMs = Distribution(frames.Select(frame => frame.ExecuteMs)),
            GpuMs = Distribution(frames.Select(frame => frame.Gpu.ElapsedMs)),
            GpuCommandSumMs = Distribution(frames.Select(frame => frame.Gpu.Passes.Sum(pass => pass.CommandsMs))),
            GpuStagesMs = stages.ToDictionary(stage => stage, stage => Distribution(frames.Select(frame =>
                frame.Gpu.Passes.Where(pass => (pass.Group ?? pass.Label) == stage).Sum(pass => pass.CommandsMs)))),
            GpuQueueCommandsMs = queues.ToDictionary(queue => queue, queue => Distribution(frames.Select(frame =>
                frame.Gpu.Passes.Where(pass => $"{pass.QueueType}:{pass.QueueIndex}" == queue).Sum(pass => pass.CommandsMs)))),
            GpuPasses = Distribution(frames.Select(frame => (double)frame.Gpu.Passes.Count)),
            TimestampQueries = Distribution(frames.Select(frame => frame.Gpu.Passes.Count * 4.0)),
            Queues = queues,
            SelectedVertexItems = Distribution(frames.Select(frame => (double)frame.Geometry.SelectedVertices)),
            SelectedTriangleItems = Distribution(frames.Select(frame => (double)frame.Geometry.SelectedTriangles)),
            PhaseOneSoftwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseOneSoftwareClusters)),
            PhaseOneHardwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseOneHardwareClusters)),
            PhaseTwoSoftwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseTwoSoftwareClusters)),
            PhaseTwoHardwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseTwoHardwareClusters)),
            CandidateClusters = Distribution(frames.Select(frame => (double)frame.Geometry.CandidateCount)),
            ShadingWorkItems = Distribution(frames.Select(frame => (double)frame.Geometry.ShadingWorkItems)),
            DeformCacheReservedBytes = Distribution(frames.Select(frame => (double)frame.Geometry.DeformCacheBytes)),
        };
    }

    private static TimingDistribution Distribution(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        return new(ordered[0], ordered.Average(), (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2,
            ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
    }

    internal readonly record struct TimingDistribution(double Min, double Mean, double Median, double P95, double Max);
    internal readonly record struct SceneFrameIndices(int First, int Last);
    internal readonly record struct SceneClockRange(double First, double Last);
    internal sealed record SceneFrameCosts
    {
        public int Frames { get; init; }
        public SceneFrameIndices FrameRange { get; init; }
        public SceneClockRange SimulationSeconds { get; init; }
        public bool AsynchronousGameUpdates { get; init; }
        public TimingDistribution CpuMs { get; init; }
        public TimingDistribution GameMs { get; init; }
        public TimingDistribution AnimationMs { get; init; }
        public TimingDistribution ExtractionMs { get; init; }
        public TimingDistribution PrepareMs { get; init; }
        public TimingDistribution ReconcileMs { get; init; }
        public TimingDistribution ExecuteMs { get; init; }
        public TimingDistribution GpuMs { get; init; }
        public TimingDistribution GpuCommandSumMs { get; init; }
        public Dictionary<string, TimingDistribution> GpuStagesMs { get; init; } = [];
        public Dictionary<string, TimingDistribution> GpuQueueCommandsMs { get; init; } = [];
        public TimingDistribution GpuPasses { get; init; }
        public TimingDistribution TimestampQueries { get; init; }
        public string[] Queues { get; init; } = [];
        public TimingDistribution SelectedVertexItems { get; init; }
        public TimingDistribution SelectedTriangleItems { get; init; }
        public TimingDistribution PhaseOneSoftwareClusters { get; init; }
        public TimingDistribution PhaseOneHardwareClusters { get; init; }
        public TimingDistribution PhaseTwoSoftwareClusters { get; init; }
        public TimingDistribution PhaseTwoHardwareClusters { get; init; }
        public TimingDistribution CandidateClusters { get; init; }
        public TimingDistribution ShadingWorkItems { get; init; }
        public TimingDistribution DeformCacheReservedBytes { get; init; }
    }
}
