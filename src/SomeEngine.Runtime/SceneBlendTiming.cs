using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

public sealed partial class SceneTestPlatform
{
    private const string BlendTimingScopes =
        "GPU milliseconds on the graphics queue, matched to the recorded SimulationFrame. " +
        "ProjectionIndexMs covers geometry projection and candidate-index construction; " +
        "CoverageReceiverMs covers hit coverage and shadow-receiver requests; " +
        "MaterialCompositeMs covers user-material evaluation and color/state composition. " +
        "These intervals are separate from presented-frame elapsed and CPU stage times. " +
        "UsedBytes counts active effect records; CapacityBytes counts effect resource descriptions; " +
        "AllocationBytes counts device-local allocation requirements. Shared mesh residency is reported separately.";

    internal sealed record SceneBlendTiming(
        int Frames, int BuildFrames, int ReuseFrames,
        GpuPhaseTiming ProjectionIndex, GpuPhaseTiming CoverageReceiver, GpuPhaseTiming MaterialComposite,
        double MeanTriangles, double MeanReferences,
        double MeanUsedBytes, ulong PeakUsedBytes, ulong PeakCapacityBytes, ulong PeakAllocationBytes)
    {
        internal static SceneBlendTiming? FromFrames(ClusterBlendMetrics[] frames)
        {
            if (frames.Length == 0) return null;
            return new(frames.Length, frames.Count(frame => !frame.ReusedIndex), frames.Count(frame => frame.ReusedIndex),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.ProjectionIndexMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.CoverageReceiverMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.MaterialCompositeMs)),
                frames.Average(frame => frame.Triangles), frames.Average(frame => frame.References),
                frames.Average(frame => (double)frame.UsedBytes), frames.Max(frame => frame.UsedBytes),
                frames.Max(frame => frame.CapacityBytes), frames.Max(frame => frame.AllocationBytes));
        }
    }

    internal sealed record GpuPhaseTiming(double MeanMs, double MedianMs, double P95Ms)
    {
        internal static GpuPhaseTiming FromValues(IEnumerable<double> values)
        {
            double[] sorted = values.Order().ToArray();
            return new(sorted.Average(), sorted[sorted.Length / 2], sorted[(int)((sorted.Length - 1) * .95)]);
        }
    }
}
