using System.Text.Json;
using SomeEngine.Runtime;

namespace DefaultGame;

internal sealed class SceneGpuTimes(SceneTestPlatform platform)
{
    private readonly Dictionary<(string Scene, bool Measurement, string Pass), PassTimes> _passes = [];

    internal void Add(ApplicationFrameTiming frame)
    {
        bool measurement = platform.IsMeasuring;
        if (measurement && (platform.Current.MeasurementFrames is not { } window ||
            frame.Frame < window.FirstFrame || frame.Frame >= window.FirstFrame + window.FrameCount)) return;
        AddTime("Frame", 0, frame.Gpu.ElapsedMs);
        foreach (var pass in frame.Gpu.Passes)
            AddTime(pass.Label, pass.CommandsMs, pass.EndMs - pass.StartMs);

        void AddTime(string name, double commands, double elapsed)
        {
            var key = (platform.Current.Name, measurement, name);
            _passes.TryGetValue(key, out PassTimes previous);
            _passes[key] = new(previous.Samples + 1, previous.CommandsMs + commands, previous.ElapsedMs + elapsed,
                Math.Max(previous.MaximumCommandsMs, commands), commands, elapsed);
        }
    }

    internal void Write(string path)
    {
        using var output = File.Create(path);
        using var json = new Utf8JsonWriter(output, new() { Indented = true });
        json.WriteStartArray();
        foreach (var (key, value) in _passes)
        {
            json.WriteStartObject();
            json.WriteString("Scene", key.Scene);
            json.WriteString("Scope", key.Measurement ? "Measurement" : "PresentedFrames");
            json.WriteString("Pass", key.Pass);
            json.WriteNumber("Samples", value.Samples);
            json.WriteNumber("MeanCommandsMs", value.CommandsMs / value.Samples);
            json.WriteNumber("MeanElapsedMs", value.ElapsedMs / value.Samples);
            json.WriteNumber("MaximumCommandsMs", value.MaximumCommandsMs);
            json.WriteNumber("LastCommandsMs", value.LastCommandsMs);
            json.WriteNumber("LastElapsedMs", value.LastElapsedMs);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private readonly record struct PassTimes(int Samples, double CommandsMs, double ElapsedMs,
        double MaximumCommandsMs, double LastCommandsMs, double LastElapsedMs);
}
