using System.Runtime.CompilerServices;

namespace SomeEngine.Diagnostics;

/// <summary>Engine instrumentation. Aggregation and analysis belong to external tools.</summary>
public static class EngineTrace
{
    private static TraceCapture? s_capture;
    [ThreadStatic] private static TraceWriter? s_writer;

    public static bool IsEnabled => Volatile.Read(ref s_capture) is { IsRecording: true };
    public static TraceContext Context
    {
        get
        {
            TraceCapture? capture = Volatile.Read(ref s_capture);
            TraceWriter? writer = s_writer;
            return capture is { IsRecording: true } && writer is not null && ReferenceEquals(writer.Capture, capture)
                ? writer.Context : default;
        }
    }

    public static TraceCapture Start(string path, TraceOptions? options = null)
    {
        lock (TraceCapture.Gate)
        {
            if (s_capture is not null) throw new InvalidOperationException("A trace capture is already active.");
            var capture = new TraceCapture(path, options ?? new TraceOptions());
            Volatile.Write(ref s_capture, capture);
            return capture;
        }
    }

    internal static void Detach(TraceCapture capture)
    {
        lock (TraceCapture.Gate)
            if (ReferenceEquals(s_capture, capture)) Volatile.Write(ref s_capture, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TraceScope Begin(TraceSite site)
    {
        TraceCapture? capture = Volatile.Read(ref s_capture);
        return capture is null || !capture.Options.Scopes || (site.Detail && !capture.Options.Detail)
            ? default : GetWriter(capture)?.Begin(site, 0, TraceEventKind.Begin) ?? default;
    }

    public static TraceScope BeginFrame(long frame)
    {
        if (frame <= 0) throw new ArgumentOutOfRangeException(nameof(frame));
        TraceCapture? capture = Volatile.Read(ref s_capture);
        return capture is null ? default : GetWriter(capture)?.Begin(TraceSite.Frame, frame, TraceEventKind.Frame) ?? default;
    }

    /// <summary>Copy context with queued work; never move an open scope between threads.</summary>
    public static TraceScope Associate(TraceContext context)
    {
        TraceCapture? capture = Volatile.Read(ref s_capture);
        if (capture is null || !capture.Options.Scopes || (context != default && context.Capture != capture.Id)) return default;
        return GetWriter(capture)?.Associate(context) ?? default;
    }

    public static void Counter(TraceSite site, long value)
    {
        TraceCapture? capture = Volatile.Read(ref s_capture);
        if (capture is null || (site.Detail && !capture.Options.Detail)) return;
        GetWriter(capture)?.Counter(site, value);
    }

    public static void NameThread(string name)
    {
        TraceCapture? capture = Volatile.Read(ref s_capture);
        if (capture is not null) GetWriter(capture)?.SetName(name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TraceWriter? GetWriter(TraceCapture capture)
    {
        if (s_writer is { } writer && ReferenceEquals(writer.Capture, capture)) return writer;
        return s_writer = capture.RegisterThread();
    }

    public readonly struct TraceScope : IDisposable
    {
        private readonly TraceWriter? _writer;
        private readonly long _id;
        internal TraceScope(TraceWriter writer, long id) => (_writer, _id) = (writer, id);
        public bool IsActive => _writer is not null;

        public void Dispose()
        {
            _writer?.End(_id);
        }
    }
}

public readonly record struct TraceContext(long Capture, long Span, long Frame);
