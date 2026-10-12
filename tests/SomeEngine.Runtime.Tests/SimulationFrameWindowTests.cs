using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class SimulationFrameWindowTests
{
    [Fact]
    public void RequestedFramesRetainSlowTimingsAndReportIncompleteReadiness()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                [{"Name":"animated scene","ContentDirectory":".","Animate":true,
                  "MeasurementFrames":{"FirstFrame":4,"FrameCount":3}}]
                """);
            TestScene scene = Assert.Single(SceneTestPlatform.ReadScenes(path));
            Assert.True(scene.Animate);
            SimulationFrameWindow window = Assert.IsType<SimulationFrameWindow>(scene.MeasurementFrames);
            Assert.Equal(4, window.FirstFrame);
            Assert.Equal(3, window.FrameCount);

            Assert.False(window.SelectFrame(3, 0, warmupComplete: false, ready: false).Collect);
            var samples = new List<(int Frame, double ElapsedMs)>();
            string? error = null;
            (int Frame, double ElapsedMs)[] input = [(4, 1.125), (5, 999.5), (6, 2.25)];
            foreach (var frame in input)
            {
                var selection = window.SelectFrame(frame.Frame, samples.Count,
                    warmupComplete: true, ready: frame.Frame != 5);
                if (selection.Collect) samples.Add(frame);
                error ??= selection.Error;
                Assert.Equal(frame.Frame == 6, selection.Finished);
            }
            Assert.Equal(input, samples);
            Assert.NotNull(error);
            Assert.False(window.SelectFrame(7, samples.Count, warmupComplete: true, ready: true).Collect);
            Assert.NotNull(window.SelectFrame(4, 0, warmupComplete: false, ready: true).Error);
            Assert.NotNull(window.SelectFrame(6, 1, warmupComplete: true, ready: true).Error);
            Assert.NotNull(window.SelectFrame(7, 2, warmupComplete: true, ready: true).Error);

            Assert.Null(window.SelectFrame(4, 0, warmupComplete: true, ready: true).Error);
            var last = window.SelectFrame(6, 2, warmupComplete: true, ready: true);
            Assert.True(last.Collect);
            Assert.True(last.Finished);
            Assert.Null(last.Error);
        }
        finally { File.Delete(path); }
    }
}
