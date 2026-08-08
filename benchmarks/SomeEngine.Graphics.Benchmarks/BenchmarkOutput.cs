using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Graphics.Benchmarks;

internal static class BenchmarkOutput
{
    internal static string FixedHash(GraphicsWorkload workload, string shaderManifestSha256)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            $"SomeEngine/RHI/RHI-EVID-003/{workload}/seed=0x5EED/{shaderManifestSha256}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static WorkloadRun Complete(
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
        NativeSetterEvidence nativeSetters)
    {
        double[] cpu = samples.Select(static sample => sample.CpuMicroseconds).ToArray();
        double[] gpu = samples
            .Where(static sample => sample.GpuMicroseconds.HasValue)
            .Select(static sample => sample.GpuMicroseconds!.Value)
            .ToArray();
        RunDisposition disposition = profile == BenchmarkProfile.VendorCertification
            ? RunDisposition.Passed
            : RunDisposition.FunctionalOnly;
        string reason = profile switch
        {
            BenchmarkProfile.WarpFunctional =>
                "Reduced-count WARP functional workload executed; not performance evidence.",
            BenchmarkProfile.FastDiagnostic =>
                "Fast hardware diagnostic workload executed; never vendor-certification evidence.",
            BenchmarkProfile.VendorCertification => "Fixed vendor workload executed.",
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
            nativeSetters,
            MetricDistribution.From(cpu),
            gpu.Length == 0 ? null : MetricDistribution.From(gpu));
    }
}
