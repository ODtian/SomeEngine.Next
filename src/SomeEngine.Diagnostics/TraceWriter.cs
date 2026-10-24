using System.Diagnostics;

namespace SomeEngine.Diagnostics;

internal sealed class TraceWriter
{
    internal readonly TraceCapture Capture;
    internal readonly int ThreadId = Environment.CurrentManagedThreadId;
    internal readonly int NativeThreadId = TraceThread.CurrentNativeId;
    private TraceBlock? _block;
    private uint _sequence;
    private int _writing;
    private long _dropped;
    private string _name = Thread.CurrentThread.Name ?? "";
    private readonly TraceNesting[] _nesting = new TraceNesting[256];
    private int _depth;
    private long _span;
    internal TraceContext Context { get; private set; }
    internal TraceWriter(TraceCapture capture) => Capture = capture;
    internal long Dropped => _dropped;
    internal string Name => Volatile.Read(ref _name);
    internal void SetName(string name) => Volatile.Write(ref _name, name);
    private long NextId() => ((long)ThreadId << 32) | ++_sequence;

    internal EngineTrace.TraceScope Begin(TraceSite site, long frame, TraceEventKind kind)
    {
        if (!CanPush()) return default;
        if (kind != TraceEventKind.Frame) frame = Context.Frame;
        long id = NextId();
        long allocated = Capture.Options.Allocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
        if (!Write(new TraceEvent(Stopwatch.GetTimestamp(), id, _span,
            Context.Span == _span ? 0 : Context.Span, frame, allocated, site.Id, kind))) return default;
        _nesting[_depth++] = new TraceNesting(id, _span, Context, true);
        _span = id;
        Context = new TraceContext(Capture.Id, id, frame);
        return new EngineTrace.TraceScope(this, id);
    }

    internal EngineTrace.TraceScope Associate(TraceContext context)
    {
        if (!CanPush()) return default;
        long id = NextId();
        _nesting[_depth++] = new TraceNesting(id, _span, Context, false);
        Context = context;
        return new EngineTrace.TraceScope(this, id);
    }

    private bool CanPush()
    {
        if (!Capture.IsRecording) return false;
        if (_depth < _nesting.Length && _sequence < uint.MaxValue) return true;
        _dropped++;
        return false;
    }

    internal void End(long id)
    {
        if (ThreadId != Environment.CurrentManagedThreadId || _depth == 0 || _nesting[_depth - 1].Id != id)
        {
            Capture.RecordMisuse();
            return;
        }
        long timestamp = Stopwatch.GetTimestamp();
        long allocated = Capture.Options.Allocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
        ref readonly TraceNesting nesting = ref _nesting[--_depth];
        _span = nesting.Span;
        Context = nesting.Context;
        if (nesting.Recorded) Write(new TraceEvent(timestamp, id, 0, 0, 0, allocated, 0, TraceEventKind.End));
    }

    internal void Counter(TraceSite site, long value)
        => Write(new TraceEvent(Stopwatch.GetTimestamp(), 0, _span,
            Context.Span == _span ? 0 : Context.Span, Context.Frame, value, site.Id, TraceEventKind.Counter));

    internal bool Write(in TraceEvent value)
    {
        Interlocked.Exchange(ref _writing, 1);
        try
        {
            if (!Capture.IsRecording) return false;
            if (_block is null && !Capture.TryGetBlock(out _block)) { _dropped++; return false; }
            _block!.ThreadId = ThreadId;
            _block.Events[_block.Count++] = value;
            if (_block.Count == _block.Events.Length) { Capture.Publish(_block); _block = null; }
            return true;
        }
        finally { Volatile.Write(ref _writing, 0); }
    }

    internal void Finish()
    {
        SpinWait wait = new();
        while (Volatile.Read(ref _writing) != 0) wait.SpinOnce();
        if (_block is null) return;
        Capture.Publish(_block); _block = null;
    }
}

internal readonly record struct TraceNesting(long Id, long Span, TraceContext Context, bool Recorded);

internal sealed class TraceBlock
{
    internal const int Capacity = 4096;
    internal readonly TraceEvent[] Events = new TraceEvent[Capacity];
    internal int Count;
    internal int ThreadId;
}
