using System.Text.Json.Nodes;
using DefaultGame;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceObservationTests
{
    [Fact]
    public void TheDeviceSceneReportsItsCompletedContactGapAlongsideItsCompletedBodyMotion()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var output = new StringWriter();

        MeasurePhysicsScenes.RunDevice(output, backend, device, "face-drop");

        JsonNode result = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).Single(value => value["Workload"] is not null);
        Assert.Equal(179, result["CompletedStep"]!.GetValue<int>());
        Assert.Equal(3, result["CompletedTime"]!.GetValue<double>());
        Assert.InRange(result["Results"]!["LastBodyPosition"]![2]!.GetValue<float>(), .399f, .401f);
        Assert.InRange(result["Results"]!["MaximumPenetrationMeters"]!.GetValue<float>(), 0, .001f);
    }
}
