using System.IO;
using System.Text.Json;
using DefaultGame;
using Xunit.Abstractions;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneAccuracyTests(ITestOutputHelper report)
{
    [Fact]
    public void TheOriginalPlanarStackDeliversItsGeometricAccuracyForEveryCompletedStep()
    {
        using var output = new StringWriter();
        int exitCode = MeasurePhysicsScenes.Run(output, "2d-stack");
        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        int completed = 0;
        double maximumDepth = 0;
        JsonDocument? summary = null;
        JsonDocument? total = null;
        try
        {
            foreach (string line in lines)
            {
                using JsonDocument row = JsonDocument.Parse(line);
                JsonElement value = row.RootElement;
                if (value.TryGetProperty("Step", out JsonElement step))
                {
                    Assert.Equal(completed, step.GetInt64());
                    Assert.Equal((completed + 1) * (double)(1f / 60f), value.GetProperty("Time").GetDouble(), 12);
                    JsonElement accuracy = value.GetProperty("Results").GetProperty("OriginalAccuracy");
                    Assert.Equal(2, accuracy.GetProperty("Dimension").GetInt32());
                    Assert.Equal(21, accuracy.GetProperty("Bodies").GetInt32());
                    Assert.Equal(20, accuracy.GetProperty("DynamicBodies").GetInt32());
                    Assert.Equal(0, accuracy.GetProperty("ExcludedPairs").GetInt64());
                    Assert.Equal(210, accuracy.GetProperty("AllowedPairs").GetInt64());
                    Assert.Equal(.005, accuracy.GetProperty("ThresholdMeters").GetDouble());
                    Assert.Equal(completed + 1, accuracy.GetProperty("CompletedSteps").GetInt32());
                    Assert.Equal(180, accuracy.GetProperty("OriginalSteps").GetInt32());
                    double depth = accuracy.GetProperty("CurrentPenetrationMeters").GetDouble();
                    Assert.True(double.IsFinite(depth));
                    Assert.True(depth >= 0);
                    maximumDepth = Math.Max(maximumDepth, depth);
                    Assert.Equal(maximumDepth, accuracy.GetProperty("MaximumPenetrationMeters").GetDouble());
                    completed++;
                }
                else if (value.TryGetProperty("Workload", out _)) summary = JsonDocument.Parse(line);
                else if (value.TryGetProperty("Scenes", out _)) total = JsonDocument.Parse(line);
            }
            Assert.Equal(180, completed);
            Assert.NotNull(summary);
            Assert.NotNull(total);
            JsonElement result = summary.RootElement;
            Assert.Equal(179, result.GetProperty("CompletedStep").GetInt64());
            Assert.Equal(180 * (double)(1f / 60f), result.GetProperty("CompletedTime").GetDouble(), 12);
            Assert.InRange(maximumDepth, 0, .005);
            bool accuracySatisfied = maximumDepth <= .005;
            Assert.Equal(accuracySatisfied, result.GetProperty("OriginalAccuracySatisfied").GetBoolean());
            Assert.Equal(maximumDepth, result.GetProperty("Results").GetProperty("OriginalAccuracy")
                .GetProperty("MaximumPenetrationMeters").GetDouble());
            Assert.Equal(1, total.RootElement.GetProperty("Scenes").GetInt32());
            Assert.Equal(accuracySatisfied ? 0 : 1, total.RootElement.GetProperty("OriginalAccuracyFailures").GetInt32());
            Assert.Equal(result.GetProperty("WithinBudget").GetBoolean() && accuracySatisfied ? 0 : 1, exitCode);
            report.WriteLine("OriginalSceneAccuracy=" + result.GetRawText());
        }
        finally { summary?.Dispose(); total?.Dispose(); }
    }
}
