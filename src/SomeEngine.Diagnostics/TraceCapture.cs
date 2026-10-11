using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SomeEngine.Diagnostics;

/// <summary>Bounded C# buffers with a dedicated IO thread. Producers never wait for disk IO.</summary>
public sealed class TraceCapture : IDisposable
{
    internal static readonly Lock Gate = new();
    private static long s_nextId;
    private readonly Lock _threadsGate = new();
    private readonly Lock _stopGate = new();
    private readonly List<TraceWriter> _threads = [];
    private readonly Channel<TraceBlock> _free;
    private readonly Channel<TraceBlock> _ready;
    private readonly AutoResetEvent _available;
    private readonly Thread _fileThread;
    private readonly BinaryWriter _file;
    private int _recording = 1;
    private bool _finished;
    private bool _disposed;
    private Exception? _failure;
    private long _misuse;
    private long _writtenEvents;

    internal TraceCapture(string path, TraceOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Trace storage is little-endian.");
        if (options.BufferMegabytes is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(options.BufferMegabytes));
        Options = options;
        Id = Interlocked.Increment(ref s_nextId);
        int blocks = checked((int)((long)options.BufferMegabytes * 1024 * 1024 / (56 * TraceBlock.Capacity)));
        _free = Channel.CreateBounded<TraceBlock>(blocks);
        _ready = Channel.CreateBounded<TraceBlock>(blocks);
        // Grow both bounded queues before recording so rollover cannot allocate queue segments.
        for (int i = 0; i < blocks; i++) _ready.Writer.TryWrite(new TraceBlock());
        while (_ready.Reader.TryRead(out TraceBlock? block)) _free.Writer.TryWrite(block);
        _file = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536), Encoding.UTF8);
        try
        {
            _available = new(false);
            _file.Write("SETRACE1"u8); _file.Write(2); _file.Write(Stopwatch.Frequency);
            _file.Write(Stopwatch.GetTimestamp()); _file.Write(DateTime.UtcNow.Ticks); _file.Write(Environment.ProcessId);
            _file.Write(options.Detail); _file.Write(options.Allocations);
            var metadata = new Dictionary<string, string>(options.Metadata)
            {
                ["trace.scopes"] = options.Scopes.ToString(),
                ["trace.bufferMegabytes"] = options.BufferMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            _file.Write(JsonSerializer.Serialize(metadata, TraceJsonContext.Default.DictionaryStringString));
            _fileThread = new Thread(WriteFile) { IsBackground = true, Name = "SomeEngine trace IO" };
            _fileThread.Start();
        }
        catch { _available?.Dispose(); _file.Dispose(); throw; }
    }

    internal long Id { get; }
    internal bool IsRecording => Volatile.Read(ref _recording) != 0;
    public TraceOptions Options { get; }
    public long WrittenEvents => Interlocked.Read(ref _writtenEvents);
    internal TraceWriter? RegisterThread()
    {
        lock (_threadsGate)
        {
            if (!IsRecording) return null;
            var writer = new TraceWriter(this); _threads.Add(writer); return writer;
        }
    }
    internal bool TryGetBlock(out TraceBlock? block) => _free.Reader.TryRead(out block);
    internal void RecordMisuse() => Interlocked.Increment(ref _misuse);
    internal void Publish(TraceBlock block)
    {
        if (!_ready.Writer.TryWrite(block))
            throw new InvalidOperationException("Trace block pool accounting failed.");
        _available.Set();
    }

    private void WriteFile()
    {
        try
        {
            while (true)
            {
                while (_ready.Reader.TryRead(out TraceBlock? block))
                {
                    _file.Write((byte)1); _file.Write(block.ThreadId); _file.Write(block.Count);
                    _file.Write(MemoryMarshal.AsBytes(block.Events.AsSpan(0, block.Count)));
                    Interlocked.Add(ref _writtenEvents, block.Count);
                    block.Count = 0; _free.Writer.TryWrite(block);
                }
                if (Volatile.Read(ref _finished) && _ready.Reader.Count == 0) break;
                _available.WaitOne();
            }
        }
        catch (Exception error) { _failure = error; Interlocked.Exchange(ref _recording, 0); }
    }

    public void Dispose()
    {
        lock (_stopGate)
        {
            if (_disposed) return;
            _disposed = true;
            EngineTrace.Detach(this);
            Interlocked.Exchange(ref _recording, 0);
            long stopped = Stopwatch.GetTimestamp();
            lock (_threadsGate) foreach (TraceWriter thread in _threads) thread.Finish();
            Volatile.Write(ref _finished, true); _available.Set(); _fileThread.Join();
            try
            {
                if (_failure is not null) throw new IOException("Trace file writing failed.", _failure);
                _file.Write((byte)2); _file.Write(stopped); _file.Write(_misuse);
                TraceSite[] sites = TraceSite.GetSites(); _file.Write(sites.Length);
                foreach (TraceSite site in sites)
                { _file.Write(site.Id); _file.Write(site.Category); _file.Write(site.Name); _file.Write(site.Detail); }
                _file.Write(_threads.Count);
                foreach (TraceWriter thread in _threads)
                { _file.Write(thread.ThreadId); _file.Write(thread.NativeThreadId); _file.Write(thread.Name); _file.Write(thread.Dropped); }
                _file.Write(WrittenEvents);
            }
            finally
            {
                _file.Dispose(); _available.Dispose();
                while (_free.Reader.TryRead(out _)) { }
                while (_ready.Reader.TryRead(out _)) { }
            }
        }
    }
}
