using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.Liquid;

public static class LiquidMeasurements
{
    public static void WriteDynamics(string path, LiquidFlow flow)
    {
        var frames = new List<object>();
        double elapsed = 0, peakSpeed = 0, volumeError = 0;
        int peakPoints = 0;
        for (int frame = 0; frame <= 360; frame++)
        {
            if (frame > 0)
            {
                for (int step = 0; step < 4; step++)
                {
                    long started = Stopwatch.GetTimestamp();
                    flow.Step();
                    elapsed += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    foreach (ref readonly LiquidPoint point in flow.Points) peakSpeed = Math.Max(peakSpeed, point.Velocity.Length());
                    peakPoints = Math.Max(peakPoints, flow.Count);
                    volumeError = Math.Max(volumeError, Math.Abs(flow.InjectedVolume - flow.Volume - flow.EvaporatedVolume - flow.AbsorbedVolume));
                }
            }
            LiquidPoint[] points = flow.Points.ToArray();
            foreach (LiquidPoint point in points) peakSpeed = Math.Max(peakSpeed, point.Velocity.Length());
            peakPoints = Math.Max(peakPoints, points.Length);
            volumeError = Math.Max(volumeError, Math.Abs(flow.InjectedVolume - flow.Volume - flow.EvaporatedVolume - flow.AbsorbedVolume));
            frames.Add(new { flow.Seconds, flow.Volume, flow.InjectedVolume, flow.EvaporatedVolume, flow.AbsorbedVolume, Points = points });
        }
        Write(path, new
        {
            flow.Scene, flow.Fluid, flow.Source, flow.Material, flow.Wetting, flow.Gravity,
            flow.PointLimit, flow.Surface, flow.Obstacles, LengthUnit = "m", VolumeUnit = "m3", FrameHz = 30,
            MeanStepMilliseconds = elapsed / 1440, PeakSpeed = peakSpeed, PeakPoints = peakPoints, MaximumVolumeError = volumeError,
            Frames = frames,
        });
        Console.WriteLine($"{flow.Fluid}/{flow.Scene}: {peakPoints} points, {peakSpeed:F4} m/s peak, {volumeError:E3} m3 volume error, {elapsed / 1440:F4} ms/step");
    }

    public static void WriteSettling(string path, int pointLimit)
    {
        var flow = new LiquidFlow(pointLimit);
        var samples = new List<object>();
        foreach (int seconds in new[] { 3, 6, 10, 20 })
        {
            while (flow.Seconds < seconds) flow.Step();
            Vector3 center = Vector3.Zero;
            double kineticEnergy = 0;
            int resting = 0;
            foreach (ref readonly LiquidPoint point in flow.Points)
            {
                center += point.Position * (float)(point.Volume / flow.Volume);
                kineticEnergy += .5 * flow.Material.Density * point.Volume * point.Velocity.LengthSquared();
                if (point.Motion == LiquidMotion.Resting) resting++;
            }
            samples.Add(new { flow.Seconds, flow.Count, flow.Volume, Resting = resting, Center = center, KineticEnergy = kineticEnergy });
        }
        Write(path, new { flow.PointLimit, Samples = samples });
    }

    private static void Write(string path, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            IncludeFields = true, Converters = { new JsonStringEnumConverter() },
        }));
    }
}
