using System.Diagnostics;
using System.Text.Json;

namespace SomeEngine.Assets.Importers;

/// <summary>Reports shader compilations and optional stage timings during a development command.</summary>
public sealed class ShaderBuildMeasurements : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly TextWriter _output;
    private readonly string? _path;
    private readonly List<Measurement> _measurements = [];
    private int _compilations;
    private int _disposed;

    private ShaderBuildMeasurements(TextWriter output, string? path)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        path = string.IsNullOrEmpty(path) ? null : path;
        _path = path;
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SomeEngine.Shaders",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                path is not null || options.Name == "Shader.Compile"
                    ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "Shader.Compile") Interlocked.Increment(ref _compilations);
                if (_path is null) return;
                var measurement = new Measurement(activity.OperationName, activity.Duration.TotalSeconds,
                    activity.TagObjects.ToDictionary(static tag => tag.Key, static tag => tag.Value));
                lock (_measurements) _measurements.Add(measurement);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Uses SOMEENGINE_SHADER_MEASUREMENTS for the optional JSON output path.</summary>
    public static ShaderBuildMeasurements Start(TextWriter output)
        => new(output, Environment.GetEnvironmentVariable("SOMEENGINE_SHADER_MEASUREMENTS"));

    /// <summary>Uses an explicit JSON output path, or only reports the compilation count when null.</summary>
    public static ShaderBuildMeasurements Start(TextWriter output, string? measurementPath)
        => new(output, measurementPath);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _listener.Dispose();
        _output.WriteLine($"Shader compilations: {Volatile.Read(ref _compilations)}");
        if (_path is not null)
        {
            Measurement[] measurements;
            lock (_measurements) measurements = _measurements.ToArray();
            string json = JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true });
            if (_path == "-") _output.WriteLine(json);
            else File.WriteAllText(_path, json);
        }
    }

    private sealed record Measurement(string Stage, double Seconds, Dictionary<string, object?> Tags);
}
