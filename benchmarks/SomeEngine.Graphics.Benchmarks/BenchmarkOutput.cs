using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Graphics.Benchmarks;

public static class BenchmarkOutput
{
    internal static string FixedHash(GraphicsWorkload workload, string shaderManifestSha256)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            $"SomeEngine/RHI/RHI-EVID-003/{workload}/seed=0x5EED/{shaderManifestSha256}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static WorkloadRun Complete(
        GraphicsWorkload workload,
        BenchmarkProfile profile,
        int warmupFrames,
        int measuredFrames,
        int drawCount,
        int barrierCount,
        FrameSample[] samples,
        CalibrationRecord[] calibrations,
        string outputSha256,
        string shaderManifestSha256,
        BarrierEvidence[] barriers,
        CommandWorkloadEvidence? workloadEvidence = null)
    {
        double[] cpu = samples.Select(static sample => sample.CpuMicroseconds).ToArray();
        double[] gpu = samples
            .Where(static sample => sample.GpuMicroseconds.HasValue)
            .Select(static sample => sample.GpuMicroseconds!.Value)
            .ToArray();
        double[] postCloseCleanup = samples
            .Where(static sample => sample.PostCloseCleanupMicroseconds.HasValue)
            .Select(static sample => sample.PostCloseCleanupMicroseconds!.Value)
            .ToArray();
        RunDisposition disposition = RunDisposition.FunctionalOnly;
        string reason = profile switch
        {
            BenchmarkProfile.WarpFunctional =>
                "Reduced-count WARP functional workload executed; not performance evidence.",
            BenchmarkProfile.FastDiagnostic =>
                "Fast hardware diagnostic workload executed; timings are diagnostic.",
            BenchmarkProfile.DeveloperProbe =>
                "Developer probe workload executed; timings are diagnostic.",
            BenchmarkProfile.RepresentativeCpuFrame =>
                "Public-source representative CPU frame workload executed with one public Draw call per draw and without Queue submission.",
            BenchmarkProfile.GraphicsCpuDevelopment =>
                "Public-source RenderGraph plus RHI development workload executed with real D3D12 commands and Queue submission.",
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        return new WorkloadRun(
            workload,
            disposition,
            reason,
            warmupFrames,
            measuredFrames,
            drawCount,
            barrierCount,
            samples,
            calibrations,
            outputSha256,
            shaderManifestSha256,
            barriers,
            MetricDistribution.From(cpu),
            gpu.Length == 0 ? null : MetricDistribution.From(gpu),
            postCloseCleanup.Length == 0
                ? null
                : MetricDistribution.From(postCloseCleanup),
            workloadEvidence);
    }
}
