using Stopwatch = System.Diagnostics.Stopwatch;
using SomeEngine.Diagnostics;
using SomeEngine.Graphics;

namespace SomeEngine.Runtime;

public sealed record ApplicationStartupStage(string Stage, double StartedMs, double CompletedMs)
{
    public double ElapsedMs => CompletedMs - StartedMs;
}

public sealed record ApplicationStartupTimes(
    int FirstSceneFrame,
    double ApplicationToFirstFrameMs,
    double? ApplicationToWindowMs,
    double? WindowToFirstFrameMs,
    IReadOnlyList<ApplicationStartupStage> Stages);

internal sealed class ApplicationStartupTiming(long started) : IDisposable
{
    private readonly List<ApplicationStartupStage> _stages = [];
    private double _stageStartedMs;
    private TraceCapture? _trace;

    internal ApplicationStartupTimes? Result { get; private set; }

    internal void StartTrace(string? path)
    {
        if (path is null) return;
        _trace = EngineTrace.Start(path, new TraceOptions
        {
            Detail = true,
            Metadata = new Dictionary<string, string> { ["application"] = "DefaultGame", ["phase"] = "startup" },
        });
    }

    internal void RecordStage(string stage)
    {
        if (Result is not null) return;
        double completedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (_stages) _stages.Add(new(stage, _stageStartedMs, completedMs));
        _stageStartedMs = completedMs;
    }

    internal void RecordWork(string stage, long workStarted)
    {
        if (Result is not null) return;
        double completedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (_stages) _stages.Add(new(stage,
            Stopwatch.GetElapsedTime(started, workStarted).TotalMilliseconds, completedMs));
    }

    internal void RecordPresent(NativeWindow window, PresentStatus status, int frame)
    {
        if (Result is not null || status is not (PresentStatus.Success or PresentStatus.Suboptimal)) return;
        RecordStage("Present");
        long presented = Stopwatch.GetTimestamp();
        Result = new(frame, Stopwatch.GetElapsedTime(started, presented).TotalMilliseconds,
            window.VisibleTimestamp == 0 ? null : Stopwatch.GetElapsedTime(started, window.VisibleTimestamp).TotalMilliseconds,
            window.VisibleTimestamp == 0 ? null : Stopwatch.GetElapsedTime(window.VisibleTimestamp, presented).TotalMilliseconds,
            _stages.ToArray());
        Console.WriteLine($"Startup: window to first frame {Result.WindowToFirstFrameMs:F2} ms; application to first frame {Result.ApplicationToFirstFrameMs:F2} ms.");
    }

    internal void FinishTrace()
    {
        _trace?.Dispose();
        _trace = null;
    }

    internal bool IsTracing => _trace is not null;

    public void Dispose() => FinishTrace();
}
