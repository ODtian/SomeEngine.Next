// DISPOSABLE #81 calibration instrumentation. Never merge into production delivery.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SomeEngine.Graphics;

/// <summary>Brackets application submissions using only public RHI calls.</summary>
public sealed class DiagnosticGpuTimestamps : IDisposable
{
    private const uint QueryCount = 131072;
    private static readonly ConditionalWeakTable<IGraphicsBackend, DiagnosticGpuTimestamps> Captures = new();
    private readonly IGraphicsBackend _backend;
    private readonly Dictionary<Queue, QueueData> _queues = [];
    private readonly List<Submission> _submissions = [];
    private readonly Dictionary<int, FrameIds> _sampleFrames = [];
    private readonly string _directory;
    private readonly string _scene;
    private readonly int _sceneIndex;
    private readonly object _settings;
    private FrameIds _frame = new(-1, 0, 0);
    private bool _disposed;

    public DiagnosticGpuTimestamps(IGraphicsBackend backend, Device device, string directory,
        string scene, int sceneIndex, object settings)
    {
        _backend = backend;
        _directory = directory;
        _scene = scene;
        _sceneIndex = sceneIndex;
        _settings = settings;
        try
        {
            foreach (QueueType type in Enum.GetValues<QueueType>())
            {
                Queue queue = backend.GetQueue(device, type);
                _queues.Add(queue, new QueueData(backend, device, queue, type));
            }
            Captures.Add(backend, this);
        }
        catch
        {
            foreach (QueueData data in _queues.Values) data.Dispose();
            throw;
        }
    }

    // Frame is zero-based scene iteration; SubmittedFrame is the next zero-based submission;
    // TraceFrame is the one-based BeginFrame ID. Canceled iterations have no measured sample.
    public void BeginFrame(int frame, uint submittedFrame, long traceFrame)
        => _frame = new(frame, submittedFrame, traceFrame);

    public void MarkSampled(int count)
    {
        if (count == 0) _sampleFrames.Clear();
        else
        {
            _sampleFrames.Add(_frame.Frame, _frame);
            if (_sampleFrames.Count != count) throw new InvalidDataException("GPU/CPU sample windows differ.");
        }
    }

    public static QueueCompletion Submit(IGraphicsBackend backend, Queue queue, in QueueSubmitDesc description)
        => Captures.TryGetValue(backend, out DiagnosticGpuTimestamps? capture) && !description.Commands.IsEmpty
            ? capture.Submit(queue, description) : backend.Submit(queue, description);

    private QueueCompletion Submit(Queue queue, in QueueSubmitDesc description)
    {
        QueueData data = _queues[queue];
        uint query = data.UsedQueries;
        if (query + 2 > QueryCount) throw new InvalidOperationException("GPU timestamp query capacity exceeded.");
        data.UsedQueries += 2;
        _backend.Begin(data.StartCommands);
        RecordedCommands start;
        try
        {
            _backend.ResetQueries(data.StartCommands, data.Queries, query, 2);
            _backend.WriteTimestamp(data.StartCommands, data.Queries, query);
            start = _backend.End(data.StartCommands);
        }
        catch { _backend.Discard(data.StartCommands); throw; }
        using (start)
        {
            _backend.Begin(data.EndCommands);
            RecordedCommands end;
            try
            {
                _backend.WriteTimestamp(data.EndCommands, data.Queries, query + 1);
                _backend.ResolveQueries(data.EndCommands, data.Queries, query, 2,
                    data.Results, new BufferRange(query * 8UL, 16));
                end = _backend.End(data.EndCommands);
            }
            catch { _backend.Discard(data.EndCommands); throw; }
            using (end)
            {
                RecordedCommands[] commands = new RecordedCommands[description.Commands.Length + 2];
                commands[0] = start;
                description.Commands.CopyTo(commands.AsSpan(1));
                commands[^1] = end;
                // Exactly one original Submit, same queue, waits, images, signals and original order.
                QueueCompletion completion = _backend.Submit(queue, new QueueSubmitDesc(
                    description.CompletionWaits, description.TimelineWaits, commands,
                    description.SwapchainImages, description.TimelineSignals));
                data.LastSubmission = completion;
                _submissions.Add(new(_frame, queue, query, description.Commands.Length));
                return completion;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Captures.Remove(_backend);
        try
        {
            var intervals = new List<Interval>();
            double originMs = _queues.Values.Min(data => data.Clock.CpuCounter * 1000.0 / data.Clock.CpuFrequency);
            foreach ((Queue queue, QueueData data) in _queues)
            {
                if (data.LastSubmission != default &&
                    _backend.WaitCpu(data.LastSubmission, TimeSpan.FromSeconds(10)) != WaitStatus.Completed)
                    throw new TimeoutException("GPU timestamp readback did not complete.");
                data.EndClock = _backend.CalibrateTimestamps(queue);
                if (data.UsedQueries == 0) continue;
                BufferRange range = new(0, data.UsedQueries * 8UL);
                using MappedBuffer mapped = _backend.Map(data.Results, MapType.Read, range);
                mapped.Invalidate(range);
                foreach (Submission submission in _submissions)
                {
                    if (submission.Queue != queue || !_sampleFrames.ContainsKey(submission.Ids.Frame)) continue;
                    int offset = checked((int)submission.Query * 8);
                    ulong start = BinaryPrimitives.ReadUInt64LittleEndian(mapped.Bytes.Slice(offset, 8));
                    ulong end = BinaryPrimitives.ReadUInt64LittleEndian(mapped.Bytes.Slice(offset + 8, 8));
                    if (end < start || start == 0) throw new InvalidDataException("Invalid GPU timestamp interval.");
                    double cpuMs = data.Clock.CpuCounter * 1000.0 / data.Clock.CpuFrequency - originMs;
                    double startMs = cpuMs + SignedDelta(start, data.Clock.QueueCounter) * 1000 / data.Clock.QueueFrequency;
                    double endMs = cpuMs + SignedDelta(end, data.Clock.QueueCounter) * 1000 / data.Clock.QueueFrequency;
                    intervals.Add(new(submission.Ids, data.Type.ToString(), submission.Query,
                        submission.CommandCount, start, end, startMs, endMs,
                        (end - start) * 1000.0 / data.Clock.QueueFrequency));
                }
            }
            var frames = intervals.GroupBy(item => item.Ids.Frame).OrderBy(group => group.Key).Select(group => new
            {
                Ids = _sampleFrames[group.Key],
                GpuSpanMs = group.Max(item => item.EndMs) - group.Min(item => item.StartMs),
                GraphicsMs = group.Where(item => item.Queue == "Graphics").Sum(item => item.DurationMs),
                ComputeMs = group.Where(item => item.Queue == "Compute").Sum(item => item.DurationMs),
                CopyMs = group.Where(item => item.Queue == "Copy").Sum(item => item.DurationMs),
                SubmissionCount = group.Count(),
            }).ToArray();
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, $"scene-{_sceneIndex}.json"), JsonSerializer.Serialize(new
            {
                Complete = frames.Length > 0 && frames.Length == _sampleFrames.Count,
                SourceSha = Environment.GetEnvironmentVariable("RG81_SOURCE_SHA"),
                DiagnosticSha = Environment.GetEnvironmentVariable("RG81_DIAGNOSTIC_SHA"),
                Scene = _scene, SceneIndex = _sceneIndex,
                Scope = "Application RenderGraph normal/late-release and Runtime UI submissions, not presentation/whole-device activity",
                Measurement = "Earliest start to latest end calibrated queue span includes gaps; per-queue sums are timestamp-bracketed submission intervals, not shader-only busy time",
                MarkerCommandListsPerSubmission = 2, QueryCountPerQueue = QueryCount,
                QueryBytesPerQueue = QueryCount * 8UL, CpuOriginMs = originMs,
                StopwatchFrequency = Stopwatch.Frequency,
                CpuSampleFrames = _sampleFrames.Count, Frames = frames.Length,
                Settings = _settings,
                Clocks = _queues.Values.Select(data => new
                {
                    Queue = data.Type.ToString(), Start = data.Clock, End = data.EndClock,
                    data.UsedQueries, ElementBytes = data.Queries.ResultInfo.ElementSize,
                    StrideBytes = data.Queries.ResultInfo.ResultStride,
                }).ToArray(),
                Samples = frames, Intervals = intervals,
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        finally { foreach (QueueData data in _queues.Values) data.Dispose(); }
    }

    private static double SignedDelta(ulong value, ulong origin)
        => value >= origin ? (double)(value - origin) : -(double)(origin - value);

    private readonly record struct FrameIds(int Frame, uint SubmittedFrame, long TraceFrame);
    private readonly record struct Submission(FrameIds Ids, Queue Queue, uint Query, int CommandCount);
    private readonly record struct Interval(FrameIds Ids, string Queue, uint Query, int CommandCount,
        ulong Start, ulong End, double StartMs, double EndMs, double DurationMs);

    private sealed class QueueData : IDisposable
    {
        internal readonly QueueType Type;
        internal readonly CalibratedTimestampInfo Clock;
        internal CalibratedTimestampInfo EndClock;
        internal QueryPool Queries = null!;
        internal Buffer Results = null!;
        internal CommandContext StartCommands = null!;
        internal CommandContext EndCommands = null!;
        internal uint UsedQueries;
        internal QueueCompletion LastSubmission;

        internal QueueData(IGraphicsBackend backend, Device device, Queue queue, QueueType type)
        {
            Type = type;
            Clock = backend.CalibrateTimestamps(queue);
            if (Clock.CpuFrequency <= 0 || Clock.QueueFrequency == 0)
                throw new InvalidDataException("GPU calibration frequencies must be positive.");
            try
            {
                Queries = backend.CreateQueryPool(device, new QueryPoolDesc(QueryType.Timestamp, type, QueryCount));
                if (Queries.ResultInfo.ElementSize != 8 || Queries.ResultInfo.ResultStride != 8)
                    throw new NotSupportedException("Diagnostic capture requires 64-bit packed timestamps.");
                Results = backend.CreateBuffer(device, new BufferDesc(QueryCount * 8UL, BufferUsages.QueryResolve), MemoryType.Readback);
                StartCommands = backend.CreateCommandContext(device, new CommandContextDesc(type, 0, 2));
                EndCommands = backend.CreateCommandContext(device, new CommandContextDesc(type, 0, 2));
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            StartCommands?.Dispose();
            EndCommands?.Dispose();
            Results?.Dispose();
            Queries?.Dispose();
        }
    }
}
