namespace SomeEngine.Runtime;

/// <summary>
/// A zero-based, inclusive simulation-frame interval chosen before comparing runs.
/// Frames keep advancing from scene load; this window never changes animation time.
/// </summary>
public sealed record SimulationFrameWindow
{
    public SimulationFrameWindow(int firstFrame, int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        _ = checked(firstFrame + frameCount - 1);
        FirstFrame = firstFrame;
        FrameCount = frameCount;
    }

    public int FirstFrame { get; }
    public int FrameCount { get; }

    /// <summary>
    /// Selects the requested frames even when readiness fails, so a bad or slow
    /// frame is retained with an error rather than replaced by a later frame.
    /// Warmup must have completed before FirstFrame.
    /// </summary>
    public (bool Collect, bool Finished, string? Error) SelectFrame(
        int simulationFrame, int recordedFrames, bool warmupComplete, bool ready)
    {
        int lastFrame = FirstFrame + FrameCount - 1;
        if (simulationFrame < FirstFrame) return (false, false, null);
        if (simulationFrame > lastFrame)
            return (false, true, recordedFrames == FrameCount ? null :
                "The requested simulation-frame window was not fully rendered.");
        string? error = simulationFrame != FirstFrame + recordedFrames
            ? "The requested simulation-frame window contains a missing or repeated frame."
            : !warmupComplete ? "Warmup did not complete before the requested simulation-frame window."
            : !ready ? "Scene streaming was not ready within the requested simulation-frame window."
            : null;
        return (true, simulationFrame == lastFrame, error);
    }
}
