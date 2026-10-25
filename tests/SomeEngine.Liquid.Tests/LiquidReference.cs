using System.Text.Json;

namespace SomeEngine.Liquid.Tests;

internal readonly record struct ReferenceDrop(double X, double Speed, double Radius, double RadialSpeed, double Volume)
{
    public static ReferenceDrop operator +(ReferenceDrop a, ReferenceDrop b) =>
        new(a.X + b.X, a.Speed + b.Speed, a.Radius + b.Radius, a.RadialSpeed + b.RadialSpeed, a.Volume + b.Volume);
    public static ReferenceDrop operator *(ReferenceDrop a, double value) =>
        new(a.X * value, a.Speed * value, a.Radius * value, a.RadialSpeed * value, a.Volume * value);
}

/// <summary>Independent continuous equations in double precision, integrated by classical RK4.</summary>
internal sealed class LiquidReference(double density, double viscosity, double tension, double recedingDegrees, double advancingDegrees)
{
    public double Slope, Amplitude, Frequency, GravityY;
    public double RecedingDegrees = recedingDegrees, AdvancingDegrees = advancingDegrees;
    public double Density { get; } = density;
    public double Viscosity { get; } = viscosity;
    public double Tension { get; } = tension;
    public double Gradient(double x) => Slope + Amplitude * Frequency * Math.Cos(Frequency * x);
    private double CosineRange => Math.Cos(RecedingDegrees * Math.PI / 180) - Math.Cos(AdvancingDegrees * Math.PI / 180);
    private double MeanCosine => .5 * (Math.Cos(RecedingDegrees * Math.PI / 180) + Math.Cos(AdvancingDegrees * Math.PI / 180));

    public ReferenceDrop Advance(ReferenceDrop value, double seconds, double maximumStep)
    {
        int steps = (int)Math.Ceiling(seconds / maximumStep);
        double h = seconds / steps;
        for (int step = 0; step < steps; step++)
        {
            ReferenceDrop k1 = Derivative(value);
            if (k1 == default) return value;
            ReferenceDrop middle1 = value + k1 * (h / 2), k2 = Derivative(middle1);
            ReferenceDrop middle2 = value + k2 * (h / 2), k3 = Derivative(middle2);
            ReferenceDrop end = value + k3 * h, k4 = Derivative(end);
            ReferenceDrop next = value + (k1 + k2 * 2 + k3 * 2 + k4) * (h / 6);
            (double drive, double retention, double radialForce, double radialRetention) = Forces(next);
            if (Math.Abs(drive) <= retention && (CrossesZero(value.Speed, middle1.Speed) || CrossesZero(value.Speed, middle2.Speed) ||
                CrossesZero(value.Speed, end.Speed) || CrossesZero(value.Speed, next.Speed))) next = next with { Speed = 0 };
            if (Math.Abs(radialForce) <= radialRetention && (CrossesZero(value.RadialSpeed, middle1.RadialSpeed) || CrossesZero(value.RadialSpeed, middle2.RadialSpeed) ||
                CrossesZero(value.RadialSpeed, end.RadialSpeed) || CrossesZero(value.RadialSpeed, next.RadialSpeed))) next = next with { RadialSpeed = 0 };
            value = next;
        }
        return value;
    }

    private static bool CrossesZero(double from, double to) => from != 0 && from * to <= 0;

    private (double Drive, double Retention, double RadialForce, double RadialRetention) Forces(ReferenceDrop value)
    {
        double slope = Gradient(value.X), metric = Math.Sqrt(1 + slope * slope), r = value.Radius, volume = value.Volume;
        double normalGravity = Math.Max(0, -GravityY / metric);
        double centre = value.X + Liquid.LiquidFlow.ContactRadius * slope / metric;
        double q = Frequency * r / metric, q2 = q * q;
        // Fourier transform of a circular parabolic cap: 8*J2(q)/q^2.
        double average = 1 - q2 / 12 + q2 * q2 / 384 - q2 * q2 * q2 / 23040;
        double averageDerivative = -q / 6 + q * q2 / 96 - q * q2 * q2 / 3840;
        double coefficient = Amplitude * Frequency * (GravityY - Tension / Density * Frequency * Frequency);
        double drive = (GravityY * Slope + coefficient * Math.Cos(Frequency * centre) * average) / metric;
        double retention = 2 * r * Tension * CosineRange * (float)(Math.PI / 4) / (Density * volume);
        double radialForce = 16 * Tension * volume * volume / (Math.PI * r * r * r * r * r) +
            4 * Density * normalGravity * volume * volume / (3 * Math.PI * r * r * r) -
            2 * Math.PI * Tension * (1 - MeanCosine) * r +
            Density * volume * coefficient * Math.Sin(Frequency * centre) * averageDerivative / metric;
        return (drive, retention, radialForce, Math.PI * Tension * r * CosineRange);
    }

    private ReferenceDrop Derivative(ReferenceDrop value)
    {
        (double drive, double retention, double radialForce, double radialRetention) = Forces(value);
        double r = value.Radius, volume = value.Volume, height = volume / (Math.PI * r * r);
        double drag = 3 * Viscosity / (Density * height * height);
        double radialDrag = 1.5 * Viscosity * Math.PI * r * r / height;
        double acceleration = value.Speed == 0 && Math.Abs(drive) <= retention ? 0 :
            drive - drag * value.Speed - Math.CopySign(retention, value.Speed == 0 ? drive : value.Speed);
        double radialAcceleration = value.RadialSpeed == 0 && Math.Abs(radialForce) <= radialRetention ? 0 :
            (radialForce - radialDrag * value.RadialSpeed - Math.CopySign(radialRetention, value.RadialSpeed == 0 ? radialForce : value.RadialSpeed)) /
                (Density * volume / 3);
        return new(value.Speed / Math.Sqrt(1 + Math.Pow(Gradient(value.X), 2)), acceleration, value.RadialSpeed, radialAcceleration, 0);
    }
}

internal static class LiquidReferenceEvidence
{
    public static void Write(string name, object result)
    {
        string directory = Environment.GetEnvironmentVariable("LIQUID_REFERENCE_RESULTS") ??
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/liquid-dynamics/reference"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
