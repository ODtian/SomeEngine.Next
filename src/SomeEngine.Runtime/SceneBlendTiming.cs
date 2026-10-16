using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

public sealed partial class SceneTestPlatform
{
    private const string BlendTimingScopes =
        "GPU milliseconds on the graphics queue, matched to the recorded SimulationFrame. " +
        "ProjectionIndexMs covers geometry projection and candidate-index construction; " +
        "CoverageReceiverMs covers hit coverage for shadow-receiver requests; " +
        "MaterialCompositeMs covers covered-hit selection, user-material evaluation and color/state composition. " +
        "It extends through declared hardware stencil export, hardware stages and stencil import. " +
        "DecalIndexMs covers decal Clear, Count, Reserve, Scatter and Sort; " +
        "OpaqueMaterialMs sums the opaque receiver and color user-evaluation intervals, including the coupled " +
        "base material, decal attributes and lighting in those passes. " +
        "These intervals are separate from presented-frame elapsed and CPU stage times. " +
        "UsedBytes counts active effect records and index/state buffers; CapacityBytes counts their buffer descriptions; " +
        "AllocationBytes counts their RHI allocation requirements, including the private stencil uniform upload. " +
        "Decal memory is included in these totals and reported separately; decal references include requested and resident GPU counts. " +
        "Shared shader parameters use the existing uniform arena, with decal parameter slices attributed by byte range. " +
        "Shared textures, mesh residency and unified instance storage are reported separately.";

    internal sealed record SceneBlendTiming(
        int Frames, int BuildFrames, int ReuseFrames,
        GpuPhaseTiming ProjectionIndex, GpuPhaseTiming CoverageReceiver, GpuPhaseTiming MaterialComposite,
        double MeanTriangles, double MeanReferences,
        double MeanUsedBytes, ulong PeakUsedBytes, ulong PeakCapacityBytes, ulong PeakAllocationBytes,
        GpuPhaseTiming DecalIndex, GpuPhaseTiming OpaqueMaterial,
        int DecalBuildFrames, int DecalReuseFrames,
        double MeanDecalVolumes, double MeanDecalReferences, double MeanDecalResidentReferences,
        double MeanDecalUsedBytes, ulong PeakDecalUsedBytes, ulong PeakDecalCapacityBytes, ulong PeakDecalAllocationBytes)
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
                frames.Max(frame => frame.CapacityBytes), frames.Max(frame => frame.AllocationBytes),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.DecalIndexMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.OpaqueMaterialMs)),
                frames.Count(frame => !frame.ReusedDecalIndex), frames.Count(frame => frame.ReusedDecalIndex),
                frames.Average(frame => frame.DecalVolumes), frames.Average(frame => frame.DecalReferences),
                frames.Average(frame => frame.DecalResidentReferences), frames.Average(frame => (double)frame.DecalUsedBytes),
                frames.Max(frame => frame.DecalUsedBytes), frames.Max(frame => frame.DecalCapacityBytes),
                frames.Max(frame => frame.DecalAllocationBytes));
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
