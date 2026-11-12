using SomeEngine.ECS.Serialization;
using System.Buffers;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static class ArchiveComparison
{
    internal readonly record struct Library(string Name, Func<FullArchiveWorld> Create);
    internal static Library[] Libraries(int count, bool raw, Scheduler? scheduler = null)
        => [new("SomeEngine", () => new SourceFullArchiveWorld(count, raw, scheduler)),
            new("EasyEcs", () => new NativeFullArchiveWorld<EasyArchiveValues>(new EasyArchiveValues(new EasyArchiveStore()), count, raw)),
            new("Friflo", () => new NativeFullArchiveWorld<FrifloArchiveValues>(new FrifloArchiveValues(new FrifloArchiveStore()), count, raw)),
            new("Arch", () => new NativeFullArchiveWorld<ArchArchiveValues>(new ArchArchiveValues(new ArchArchiveStore()), count, raw)),
            new("DefaultEcs", () => new NativeFullArchiveWorld<DefaultArchiveValues>(new DefaultArchiveValues(new DefaultArchiveStore()), count, raw))];

    internal static void CheckAll(int count, bool saveOnly = false, string? formatFilter = null)
    {
        foreach (bool raw in new[] { true, false })
        foreach (bool preserve in new[] { true, false })
        foreach (Library library in Libraries(count, raw))
        {
            if (!saveOnly && formatFilter is null) using (FullArchiveWorld world = library.Create())
            {
                using IDisposable snapshot = world.Capture(preserve);
                world.Mutate(); world.Restore(snapshot); world.Check(preserve);
                world.Complete(); world.Check(preserve, true);
                world.Restore(snapshot); world.Check(preserve);
            }
            foreach (string format in formatFilter is not null ? new[] { formatFilter } : saveOnly ? new[] { "span", "stream" } : new[] { "span", "stream", "background", "file" })
            {
                using FullArchiveWorld world = library.Create();
                var output = new ArrayBufferWriter<byte>(); using var stream = new MemoryStream();
                string path = Path.Combine("artifacts", "archive-check-" + library.Name + ".save");
                try
                {
                    Task? pending = null;
                    if (format == "span") world.Save(output, preserve);
                    else if (format == "stream") world.Save(stream, preserve);
                    else if (format == "background") pending = world.SaveAsync(stream, preserve);
                    else pending = world.SaveFileAsync(path, preserve);
                    world.Mutate(); pending?.GetAwaiter().GetResult();
                    if (format == "span") world.Load(output.WrittenSpan);
                    else if (format == "file") world.LoadFile(path);
                    else { stream.Position = 0; using var input = new ArchiveSequentialStream(stream); world.Load(input); }
                    world.Check(preserve); world.Complete(); world.Check(preserve, true);
                }
                finally { File.Delete(path); File.Delete(path + ".previous"); File.Delete(path + ".lock"); }
            }
            string formats = formatFilter ?? (saveOnly ? "span/sequential" : "memory/span/sequential/background/file");
            Console.WriteLine($"{library.Name}: complete archive raw={raw}, preserve={preserve} {formats} results passed.");
        }
    }

    internal static void CheckGrowth(int count)
    {
        foreach (bool raw in new[] { true, false })
        foreach (bool preserve in new[] { true, false })
        foreach (Library library in Libraries(count, raw))
        {
            using FullArchiveWorld world = library.Create(); world.Grow();
            using (IDisposable data = world.Capture(preserve))
            { world.Mutate(); world.Restore(data); world.Check(preserve); }
            var output = new ArrayBufferWriter<byte>(); world.Save(output, preserve);
            world.Mutate(); world.Load(output.WrittenSpan); world.Check(preserve);
            world.Complete(); world.Check(preserve, true);
            Console.WriteLine($"{library.Name}: grown complete archive raw={raw}, preserve={preserve} memory/span results passed.");
        }
    }

    internal static int Run(string[] args)
    {
        string mode = args[1]; bool preserve = args[2] == "preserve", raw = args[3] == "raw";
        int count = int.Parse(args[4]); string path = Path.GetFullPath(args[5]);
        var libraries = Libraries(count, raw); var repetitions = new int[libraries.Length];
        var outputBytes = new long[libraries.Length];
        var outputCapacities = new long?[libraries.Length];
        var compactCosts = new CompactArchiveOutput.Costs?[libraries.Length];
        var samples = libraries.Select(_ => new List<ArchiveMeasurement>()).ToArray();
        for (int i = 0; i < libraries.Length; i++)
        {
            using FullArchiveWorld world = libraries[i].Create();
            using var action = new ArchiveAction(world, mode, preserve, libraries[i].Name);
            action.Run(); action.Verify(); world.Check(preserve);
            var clock = Stopwatch.StartNew(); int done = 0;
            do { action.Run(); done++; } while (clock.ElapsedMilliseconds < 300);
            repetitions[i] = Math.Max(1, (int)(done * 100.0 / clock.Elapsed.TotalMilliseconds));
        }
        for (int round = 0; round < 10; round++)
            for (int offset = 0; offset < libraries.Length; offset++)
            {
                int i = (round + offset) % libraries.Length;
                using FullArchiveWorld world = libraries[i].Create();
                using var action = new ArchiveAction(world, mode, preserve, libraries[i].Name);
                action.Run(); action.Verify(); world.Check(preserve);
                action.Run();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long bytes = GC.GetTotalAllocatedBytes(false), began = Stopwatch.GetTimestamp();
                for (int repeat = 0; repeat < repetitions[i]; repeat++) action.Run();
                samples[i].Add(new(Stopwatch.GetElapsedTime(began).TotalNanoseconds / repetitions[i],
                    (double)(GC.GetTotalAllocatedBytes(false) - bytes) / repetitions[i]));
                outputBytes[i] = action.OutputBytes;
                outputCapacities[i] = action.OutputCapacityBytes;
                compactCosts[i] = action.CompactOutputCosts;
            }
            var report = new
            {
                Workload = "archive-" + mode, Progress = preserve ? "preserve" : "rebuild", Encoding = raw ? "raw" : "codec", Entities = count,
                ReferenceValueAccess = "concrete-nonvirtual", FileService = mode == "file" ? "DurableSaveStore" : null,
                FileIntegrity = mode == "file" ? "SHA-256" : null, FileEnvelopeVersion = mode == "file" ? 4 : 0,
                FileHeaderBytes = mode == "file" ? 64 : 0, FileGenerationSlots = mode == "file" ? 2 : 0,
                Samples = 10, TargetSampleMilliseconds = 100, Framework = RuntimeInformation.FrameworkDescription,
                ServerGC = GCSettings.IsServerGC, TieredCompilation = false, TimingProcessor = Thread.GetCurrentProcessorId(),
                Results = libraries.Select((library, i) => new
                {
                    Library = library.Name, MedianNanoseconds = Median(samples[i].Select(s => s.Nanoseconds)),
                    AllocatedBytes = Median(samples[i].Select(s => s.AllocatedBytes)), OutputBytes = outputBytes[i],
                    OutputCapacityBytes = outputCapacities[i], OutputUnusedCapacityBytes = outputCapacities[i] - outputBytes[i],
                    CompactOutput = compactCosts[i],
                    Measurements = samples[i]
                })
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); Console.WriteLine(json); return 0;
    }
    private static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return (sorted[4] + sorted[5]) / 2; }
    private readonly record struct ArchiveMeasurement(double Nanoseconds, double AllocatedBytes);

    internal sealed class ArchiveAction : IDisposable
    {
        private readonly FullArchiveWorld _world;
        private readonly string _mode, _path;
        private readonly bool _preserve;
        private readonly ArrayBufferWriter<byte>? _output;
        private readonly CompactArchiveOutput? _compactOutput;
        private readonly MemoryStream _stream = new();
        private IDisposable? _snapshot;
        private byte[] _input = [];
        internal long OutputBytes => _mode == "file" ? AtomicSaveFile.PayloadLength(_path)
            : _mode == "background" ? _stream.Length : _compactOutput?.WrittenCount ?? _output!.WrittenCount;
        internal long? OutputCapacityBytes => _mode == "file" ? null
            : _mode == "background" ? _stream.Capacity : _compactOutput?.Capacity ?? _output!.Capacity;
        internal CompactArchiveOutput.Costs? CompactOutputCosts => _compactOutput?.Statistics;
        internal ArchiveAction(FullArchiveWorld world, string mode, bool preserve, string library)
        {
            _world = world; _mode = mode; _preserve = preserve;
            if (mode == "save-compact") _compactOutput = new();
            else _output = new();
            _path = Path.Combine("artifacts", "archive-performance-" + library + ".save");
            if (mode == "restore") _snapshot = world.Capture(preserve);
            if (mode == "load") { world.Save(_output!, preserve); _input = _output!.WrittenSpan.ToArray(); }
        }
        internal void Run()
        {
            switch (_mode)
            {
                case "capture": _snapshot?.Dispose(); _snapshot = _world.Capture(_preserve); break;
                case "restore": _world.Restore(_snapshot!); break;
                case "save": _output!.Clear(); _world.Save(_output, _preserve); break;
                case "save-compact":
                    _compactOutput!.Clear();
                    _world.Save(_compactOutput, _preserve);
                    _compactOutput.Complete();
                    break;
                case "load": _world.Load(_input); break;
                case "background": _stream.Position = 0; _stream.SetLength(0); _world.SaveAsync(_stream, _preserve).GetAwaiter().GetResult(); break;
                case "file": _world.SaveFileAsync(_path, _preserve).GetAwaiter().GetResult(); break;
                default: throw new ArgumentException("Choose capture, restore, save, save-compact, load, background or file.");
            }
        }
        internal void PrepareGrowth()
        {
            if (_mode == "restore") { _snapshot!.Dispose(); _snapshot = _world.Capture(_preserve); }
            if (_mode == "load") { _output!.Clear(); _world.Save(_output, _preserve); _input = _output.WrittenSpan.ToArray(); }
        }
        internal void Verify()
        {
            switch (_mode)
            {
                case "capture": _world.Restore(_snapshot!); break;
                case "save": _world.Load(_output!.WrittenSpan); break;
                case "save-compact": _world.Load(_compactOutput!.WrittenSpan); break;
                case "background": _stream.Position = 0; _world.Load(_stream); break;
                case "file": _world.LoadFile(_path); break;
            }
        }
        public void Dispose() { _snapshot?.Dispose(); _compactOutput?.Dispose(); _stream.Dispose(); File.Delete(_path); File.Delete(_path + ".previous"); File.Delete(_path + ".lock"); }
    }
}

internal sealed class ArchiveSequentialStream(Stream stream) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => stream.Read(buffer);
    public override void Flush() { }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
