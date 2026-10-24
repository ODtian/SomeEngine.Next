using System.Numerics;

namespace SomeEngine.Transparency.Research;

public interface ITransparencyRenderer : IDisposable
{
    long GeometryBytes { get; }
    long IntermediateBytes { get; }
    double Render();
    TransparencyTimes Measure(int warmupFrames, int samples);
    Vector4[] ReadColors();
}

public sealed record TransparencyTimes(double[] TotalMs, double[] FirstPassMs, double[] SecondPassMs, double[] ResolveMs);
