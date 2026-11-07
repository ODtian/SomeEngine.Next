using System.Text.Json;
using System.Text.Json.Nodes;

namespace SomeEngine.Tools;

internal sealed record QueueSettings
{
    public int CpuCoreCount { get; init; } = 4;
    public int MemoryMiB { get; init; } = 8192;
    public int TimeoutSeconds { get; init; } = 60;
    public int OutputMiB { get; init; } = 8;
    public int GpuMemoryMiB { get; init; } = 4096;
    public int MinimumIdleGpuCount { get; init; } = 2;
    public bool Local { get; init; }
    public bool RemoteOnly { get; init; }
    public string? RemoteHost { get; init; }
    public string RemoteDirectory { get; init; } = ".cache/someengine-benchmark-queue";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SomeEngine", "BenchmarkQueue");

    public static QueueSettings Read()
    {
        string sharedPath = Path.Combine(DataDirectory, "settings.json");
        JsonObject values = File.Exists(sharedPath)
            ? JsonNode.Parse(File.ReadAllText(sharedPath))!.AsObject() : new();
        string? taskPath = Environment.GetEnvironmentVariable("SOMEENGINE_BENCHMARK_CONFIG");
        if (taskPath is not null)
            foreach (var value in JsonNode.Parse(File.ReadAllText(taskPath))!.AsObject())
                values[value.Key] = value.Value?.DeepClone();
        return values.Deserialize<QueueSettings>()!;
    }
}
