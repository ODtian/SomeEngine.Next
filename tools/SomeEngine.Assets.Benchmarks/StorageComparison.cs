using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Compares immutable payload storage with identical data and verification work.
/// Input construction and file reads happen before the timed region.
/// </summary>
internal static class StorageComparison
{
    private const int SegmentBytes = 64 * 1024;
    private const int ArenaBlockBytes = 4 * 1024 * 1024;
    private const int MinimumInputBytes = 16 * 1024 * 1024;
    private const int MaximumInputBytes = 32 * 1024 * 1024;
    private const int VersionValueBytes = 4 * 1024 * 1024;
    private const int VersionCount = 13;
    private const int RetainedVersionCount = 4;
    private const int LocalChangeBytes = 4 * 1024;
    private const ulong HashSeed = 0x9E3779B185EBCA87UL;
    private const ulong HashMultiplier = 0xC2B2AE3D27D4EB4FUL;

    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            GCSettings.LatencyMode = options.Latency == "low" ? GCLatencyMode.LowLatency : GCLatencyMode.Interactive;
            Workload workload = LoadWorkload(options.CaseName, options.Root);
            using NativeArena? reusedArena = options.Kind == "native-reuse" ? new NativeArena() : null;
            StorageReading warmup = RunOnce(options.Kind, workload, reusedArena);
            Collect();

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            long managedBefore = GC.GetTotalMemory(forceFullCollection: false);
            TimeSpan pauseBefore = GC.GetTotalPauseDuration();
            int[] collectionsBefore = CollectionCounts();
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBefore = process.WorkingSet64;

            var readings = new StorageReading[options.Samples];
            long hotStarted = Stopwatch.GetTimestamp();
            for (int sample = 0; sample < readings.Length; sample++)
            {
                readings[sample] = RunOnce(options.Kind, workload, reusedArena);
                if (readings[sample].Checksum != warmup.Checksum)
                    throw new InvalidDataException("A storage implementation changed the payload checksum.");
            }
            double hotNanoseconds = Stopwatch.GetElapsedTime(hotStarted).TotalNanoseconds;

            process.Refresh();
            long workingSetBeforeReclaim = process.WorkingSet64;
            long managedBeforeReclaim = GC.GetTotalMemory(forceFullCollection: false);
            TimeSpan hotPause = GC.GetTotalPauseDuration() - pauseBefore;
            int[] hotCollections = CollectionCounts();

            long nativeFinalStarted = Stopwatch.GetTimestamp();
            reusedArena?.Dispose();
            double nativeFinalNanoseconds = reusedArena is null ? 0 : Stopwatch.GetElapsedTime(nativeFinalStarted).TotalNanoseconds;
            process.Refresh();
            long workingSetAfterNativeRelease = process.WorkingSet64;

            long reclaimStarted = Stopwatch.GetTimestamp();
            Collect();
            double reclaimNanoseconds = Stopwatch.GetElapsedTime(reclaimStarted).TotalNanoseconds;
            process.Refresh();

            long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            long managedAfterReclaim = GC.GetTotalMemory(forceFullCollection: false);
            TimeSpan allPause = GC.GetTotalPauseDuration() - pauseBefore;
            int[] allCollections = CollectionCounts();
            long workingSetAfterReclaim = process.WorkingSet64;
            long peakWorkingSet = process.PeakWorkingSet64;

            double[] build = Sorted(readings, static value => value.BuildNanoseconds);
            double[] read = Sorted(readings, static value => value.ReadNanoseconds);
            double[] release = Sorted(readings, static value => value.ReleaseNanoseconds);
            double[] endToEnd = Sorted(readings, static value => value.TotalNanoseconds);
            long copiedBytes = readings.Sum(static value => value.CopiedBytes);
            double medianSeconds = Percentile(endToEnd, .50) / 1_000_000_000d;
            long bytesPerRound = readings[0].InputBytes;

            var result = new StorageResult
            {
                Schema = 2,
                Kind = options.Kind,
                Case = options.CaseName,
                Gc = options.GcName,
                GcEvidence = DescribeGc(),
                GcLatency = GCSettings.LatencyMode.ToString(),
                ServerGc = GCSettings.IsServerGC,
                Framework = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                Samples = options.Samples,
                Source = workload.Source,
                ValueCount = workload.Values.Count,
                InputBytes = readings[0].InputBytes,
                LiveValueBytes = readings[0].LiveValueBytes,
                RetainedStorageBytes = readings[0].RetainedStorageBytes,
                StorageAllocationBytesPerRound = readings[0].CopiedBytes,
                NativeReservedBytesPerRound = readings[0].NativeReservedBytes,
                AlignmentWasteBytesPerRound = readings[0].AlignmentWasteBytes,
                AbandonedBytesPerRound = readings[0].AbandonedBytes,
                CopiedBytesPerRound = readings[0].CopiedBytes,
                ManagedAllocatedBytes = allocatedBytes,
                NativeAllocatedBytesDuringSamples = options.Kind == "native"
                    ? readings.Sum(static value => value.NativeReservedBytes)
                    : 0,
                PayloadObjectCountPerRound = readings[0].PayloadObjectCount,
                SegmentNodeCountPerRound = readings[0].SegmentNodeCount,
                TraversalBranchesPerRound = readings[0].TraversalBranches,
                Checksum = $"{warmup.Checksum:x16}",
                BuildP50Microseconds = Microseconds(build, .50),
                BuildP95Microseconds = Microseconds(build, .95),
                BuildP99Microseconds = Microseconds(build, .99),
                ReadP50Microseconds = Microseconds(read, .50),
                ReadP95Microseconds = Microseconds(read, .95),
                ReadP99Microseconds = Microseconds(read, .99),
                ReleaseP50Microseconds = Microseconds(release, .50),
                ReleaseP95Microseconds = Microseconds(release, .95),
                ReleaseP99Microseconds = Microseconds(release, .99),
                EndToEndP50Microseconds = Microseconds(endToEnd, .50),
                EndToEndP95Microseconds = Microseconds(endToEnd, .95),
                EndToEndP99Microseconds = Microseconds(endToEnd, .99),
                EndToEndMaximumMicroseconds = endToEnd[^1] / 1000d,
                PayloadMegabytesPerSecondP50 = medianSeconds == 0 ? 0 : bytesPerRound / (1024d * 1024d) / medianSeconds,
                CopyMegabytesPerSecondP50 = medianSeconds == 0 ? 0 : readings[0].CopiedBytes / (1024d * 1024d) / medianSeconds,
                HotElapsedMilliseconds = hotNanoseconds / 1_000_000d,
                ReclaimMilliseconds = reclaimNanoseconds / 1_000_000d,
                NativeFinalReleaseMilliseconds = nativeFinalNanoseconds / 1_000_000d,
                CombinedMilliseconds = (hotNanoseconds + nativeFinalNanoseconds + reclaimNanoseconds) / 1_000_000d,
                HotPauseMilliseconds = hotPause.TotalMilliseconds,
                ReclaimPauseMilliseconds = (allPause - hotPause).TotalMilliseconds,
                Gen0Collections = hotCollections[0] - collectionsBefore[0],
                Gen1Collections = hotCollections[1] - collectionsBefore[1],
                Gen2Collections = hotCollections[2] - collectionsBefore[2],
                ReclaimGen0Collections = allCollections[0] - hotCollections[0],
                ReclaimGen1Collections = allCollections[1] - hotCollections[1],
                ReclaimGen2Collections = allCollections[2] - hotCollections[2],
                ManagedBytesBefore = managedBefore,
                ManagedBytesBeforeReclaim = managedBeforeReclaim,
                ManagedBytesAfterReclaim = managedAfterReclaim,
                WorkingSetBefore = workingSetBefore,
                WorkingSetBeforeReclaim = workingSetBeforeReclaim,
                WorkingSetAfterNativeRelease = workingSetAfterNativeRelease,
                WorkingSetAfterReclaim = workingSetAfterReclaim,
                PeakWorkingSet = peakWorkingSet,
                AllocationBytesPerCopiedByte = copiedBytes == 0 ? 0 : (double)allocatedBytes / copiedBytes,
                RetentionAmplification = readings[0].LiveValueBytes == 0
                    ? 0
                    : (double)readings[0].RetainedStorageBytes / readings[0].LiveValueBytes,
                StorageAllocationAmplification = readings[0].InputBytes == 0
                    ? 0
                    : (double)readings[0].CopiedBytes / readings[0].InputBytes,
                NativeReserveAmplification = readings[0].InputBytes == 0
                    ? 0
                    : (double)readings[0].NativeReservedBytes / readings[0].InputBytes,
            };

            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            GC.KeepAlive(workload);
            return Task.FromResult(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return Task.FromResult(1);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StorageReading RunOnce(string kind, Workload workload, NativeArena? reusedArena)
        => kind switch
        {
            "array" => RunArray(workload),
            "segments" => RunSegments(workload),
            "native" => RunNative(workload),
            "native-reuse" => RunNativeCore(workload, reusedArena ?? throw new InvalidOperationException("Missing reusable arena."), freeBlocks: false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static StorageReading RunArray(Workload workload)
        => workload.IsVersionCase ? RunArrayVersions(workload) : RunArrayValues(workload);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StorageReading RunArrayValues(Workload workload)
    {
        long started = Stopwatch.GetTimestamp();
        var values = new byte[workload.Values.Count][];
        long copied = 0;
        for (int index = 0; index < values.Length; index++)
        {
            byte[] source = workload.Values[index];
            values[index] = source.AsSpan().ToArray();
            copied += source.Length;
        }
        double build = ElapsedNanoseconds(started);

        started = Stopwatch.GetTimestamp();
        ulong checksum = HashArrays(values);
        double read = ElapsedNanoseconds(started);

        started = Stopwatch.GetTimestamp();
        GC.KeepAlive(values);
        values = null!;
        double release = ElapsedNanoseconds(started);
        return new StorageReading(build, read, release, checksum, copied, copied, copied, copied,
            0, 0, 0, workload.Values.Count, 0, workload.Values.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StorageReading RunArrayVersions(Workload workload)
    {
        byte[] source = workload.Values[0];
        long started = Stopwatch.GetTimestamp();
        var versions = new Queue<byte[]>(RetainedVersionCount + 1);
        byte[] current = source.AsSpan().ToArray();
        versions.Enqueue(current);
        long copied = current.Length;
        for (int version = 1; version < VersionCount; version++)
        {
            byte[] changed = current.AsSpan().ToArray();
            ApplyChange(changed, version);
            copied += changed.Length;
            versions.Enqueue(changed);
            current = changed;
            if (versions.Count > RetainedVersionCount) _ = versions.Dequeue();
        }
        double build = ElapsedNanoseconds(started);

        started = Stopwatch.GetTimestamp();
        ulong checksum = HashArrays(versions);
        double read = ElapsedNanoseconds(started);

        long live = versions.Sum(static value => (long)value.Length);
        int objects = VersionCount;
        started = Stopwatch.GetTimestamp();
        GC.KeepAlive(versions);
        versions = null!;
        current = null!;
        double release = ElapsedNanoseconds(started);
        return new StorageReading(build, read, release, checksum, source.Length, live, live, copied,
            0, 0, 0, objects, 0, RetainedVersionCount);
    }

    private static StorageReading RunSegments(Workload workload)
        => workload.IsVersionCase ? RunSegmentVersions(workload) : RunSegmentValues(workload);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StorageReading RunSegmentValues(Workload workload)
    {
        long started = Stopwatch.GetTimestamp();
        var values = new SegmentValue[workload.Values.Count];
        long copied = 0;
        int segmentCount = 0;
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = SegmentValue.CopyFrom(workload.Values[index]);
            copied += workload.Values[index].Length;
            segmentCount += values[index].SegmentCount;
        }
        double build = ElapsedNanoseconds(started);

        started = Stopwatch.GetTimestamp();
        ulong checksum = HashSegments(values, out int branches);
        double read = ElapsedNanoseconds(started);

        started = Stopwatch.GetTimestamp();
        GC.KeepAlive(values);
        values = null!;
        double release = ElapsedNanoseconds(started);
        int objects = checked(segmentCount * 2 + workload.Values.Count);
        return new StorageReading(build, read, release, checksum, copied, copied, copied, copied,
            0, 0, 0, objects, segmentCount, branches);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StorageReading RunSegmentVersions(Workload workload)
    {
        byte[] source = workload.Values[0];
        long started = Stopwatch.GetTimestamp();
        byte[][] pieces = CopyPieces(source);
        var versions = new Queue<SegmentValue>(RetainedVersionCount + 1);
        versions.Enqueue(SegmentValue.FromPieces(pieces));
        long copied = source.Length;
        int nodes = pieces.Length;
        int arrays = pieces.Length;
        for (int version = 1; version < VersionCount; version++)
        {
            byte[][] changed = (byte[][])pieces.Clone();
            int pieceIndex = ChangeOffset(version) / SegmentBytes;
            byte[] replacement = changed[pieceIndex].AsSpan().ToArray();
            ApplyChange(replacement, version, ChangeOffset(version) % SegmentBytes);
            changed[pieceIndex] = replacement;
            pieces = changed;
            versions.Enqueue(SegmentValue.FromPieces(pieces));
            copied += replacement.Length;
            arrays++;
            nodes += pieces.Length;
            if (versions.Count > RetainedVersionCount) _ = versions.Dequeue();
        }
        double build = ElapsedNanoseconds(started);

        started = Stopwatch.GetTimestamp();
        ulong checksum = HashSegments(versions, out int branches);
        double read = ElapsedNanoseconds(started);

        long live = checked((long)RetainedVersionCount * source.Length);
        long retained = checked((long)(pieces.Length + RetainedVersionCount - 1) * SegmentBytes);
        started = Stopwatch.GetTimestamp();
        GC.KeepAlive(versions);
        versions = null!;
        pieces = null!;
        double release = ElapsedNanoseconds(started);
        int objects = checked(arrays + nodes + VersionCount);
        return new StorageReading(build, read, release, checksum, source.Length, live, retained, copied,
            0, 0, 0, objects, nodes, branches);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe StorageReading RunNative(Workload workload)
    {
        var arena = new NativeArena();
        try
        {
            return RunNativeCore(workload, arena, freeBlocks: true);
        }
        finally
        {
            arena.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe StorageReading RunNativeCore(Workload workload, NativeArena arena, bool freeBlocks)
    {
        long started = Stopwatch.GetTimestamp();
        ulong checksum;
        long inputBytes;
        long liveBytes;
        long retainedBytes;
        long copiedBytes;
        long abandonedBytes;
        int branches;
        double build;
        double read;
        if (workload.IsVersionCase)
        {
            byte[] source = workload.Values[0];
            var versions = new Queue<NativeSlice>(RetainedVersionCount + 1);
            NativeSlice current = arena.CopyFrom(source);
            versions.Enqueue(current);
            copiedBytes = source.Length;
            for (int version = 1; version < VersionCount; version++)
            {
                NativeSlice changed = arena.CopyFrom(current.Bytes);
                ApplyChange(changed.Bytes, version);
                copiedBytes += changed.Length;
                versions.Enqueue(changed);
                current = changed;
                if (versions.Count > RetainedVersionCount) _ = versions.Dequeue();
            }
            build = ElapsedNanoseconds(started);
            started = Stopwatch.GetTimestamp();
            checksum = HashNative(versions, out branches);
            read = ElapsedNanoseconds(started);
            inputBytes = source.Length;
            liveBytes = checked((long)RetainedVersionCount * source.Length);
            retainedBytes = arena.ReservedBytes;
            abandonedBytes = copiedBytes - liveBytes;
            GC.KeepAlive(versions);
        }
        else
        {
            var values = new NativeSlice[workload.Values.Count];
            copiedBytes = 0;
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = arena.CopyFrom(workload.Values[index]);
                copiedBytes += values[index].Length;
            }
            build = ElapsedNanoseconds(started);
            started = Stopwatch.GetTimestamp();
            checksum = HashNative(values, out branches);
            read = ElapsedNanoseconds(started);
            inputBytes = copiedBytes;
            liveBytes = copiedBytes;
            retainedBytes = arena.ReservedBytes;
            abandonedBytes = 0;
            GC.KeepAlive(values);
        }

        long reserved = arena.ReservedBytes;
        long waste = arena.AlignmentWasteBytes;
        int blocks = arena.BlockCount;
        started = Stopwatch.GetTimestamp();
        if (freeBlocks) arena.Dispose();
        else arena.Reset();
        double release = ElapsedNanoseconds(started);
        return new StorageReading(build, read, release, checksum, inputBytes, liveBytes, retainedBytes,
            copiedBytes, reserved, waste, abandonedBytes, blocks, 0, branches);
    }

    private static Workload LoadWorkload(string caseName, string root)
        => caseName switch
        {
            "shader" => LoadFiles(caseName, Path.Combine(root, "assets", "Shaders"), ".shader.asset"),
            "mesh" => LoadFiles(caseName, Path.Combine(root, "samples", "DefaultGame", "assets"), ".mesh.asset"),
            "texture" => LoadFiles(caseName, Path.Combine(root, "samples", "DefaultGame", "assets"), ".texture.asset"),
            "document" => LoadDocuments(root),
            "audio" => CreateRepeated(caseName, [64 * 1024, 256 * 1024, 1024 * 1024], 0xA710),
            "network" => CreateRepeated(caseName, [256, 512, 1500, 4096, 16 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024], 0x4E37),
            "small" => CreateRepeated(caseName, [64, 128, 256, 512, 1024, 4096, 16 * 1024], 0x5A11),
            "one-megabyte" => CreateRepeated(caseName, [1024 * 1024], 0x1E6),
            "mixed" => CreateRepeated(caseName, [128, 1024, 16 * 1024, 64 * 1024, 1024 * 1024, 4 * 1024 * 1024], 0xB17E),
            "churn" => CreateRepeated(caseName, [32, 48, 64, 96, 128, 256, 512, 1024, 2048], 0xC417),
            "versions" => new Workload(caseName, "deterministic 4 MiB value; twelve 4 KiB changes; four versions retained",
                [CreatePattern(VersionValueBytes, 0xC0A)], true),
            _ => throw new ArgumentException($"Unknown case '{caseName}'. Expected shader, mesh, texture, audio, document, network, small, one-megabyte, mixed, versions, or churn."),
        };

    private static Workload LoadFiles(string caseName, string directory, string suffix)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        string[] paths = Directory.EnumerateFiles(directory, $"*{suffix}", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0) throw new FileNotFoundException($"No {suffix} inputs were found under {directory}.");
        var values = new List<byte[]>();
        var names = new List<string>();
        long total = 0;
        foreach (string path in paths)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) continue;
            if (total > 0 && total + bytes.Length > MaximumInputBytes) break;
            values.Add(bytes);
            names.Add(Path.GetRelativePath(directory, path));
            total += bytes.Length;
        }
        RepeatToMinimum(values, MinimumInputBytes);
        string source = $"{names.Count.ToString(CultureInfo.InvariantCulture)} repository {suffix} files under {directory}; size distribution repeated to at least 16 MiB";
        return new Workload(caseName, source, values, false);
    }

    private static Workload LoadDocuments(string root)
    {
        var paths = new List<string>();
        foreach (string directory in new[] { Path.Combine(root, "wiki"), Path.Combine(root, "docs") })
            if (Directory.Exists(directory)) paths.AddRange(Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories));
        paths.Sort(StringComparer.Ordinal);
        if (paths.Count == 0) throw new FileNotFoundException($"No Markdown documents were found under {root}.");
        var values = new List<byte[]>();
        long total = 0;
        foreach (string path in paths)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) continue;
            if (total > 0 && total + bytes.Length > MaximumInputBytes) break;
            values.Add(bytes);
            total += bytes.Length;
        }
        RepeatToMinimum(values, MinimumInputBytes);
        return new Workload("document", $"{paths.Count.ToString(CultureInfo.InvariantCulture)} repository Markdown files; size distribution repeated to at least 16 MiB", values, false);
    }

    private static Workload CreateRepeated(string caseName, int[] sizes, int seed)
    {
        var source = sizes.Select((size, index) => CreatePattern(size, seed + index)).ToArray();
        var values = new List<byte[]>();
        int index = 0;
        long total = 0;
        while (total < MinimumInputBytes)
        {
            byte[] value = source[index++ % source.Length];
            values.Add(value);
            total += value.Length;
        }
        return new Workload(caseName, $"deterministic representative distribution: {string.Join(',', sizes)} bytes", values, false);
    }

    private static void RepeatToMinimum(List<byte[]> values, long minimumBytes)
    {
        if (values.Count == 0) throw new InvalidDataException("The selected inputs are empty.");
        int sourceCount = values.Count;
        long total = values.Sum(static value => (long)value.Length);
        int index = 0;
        while (total < minimumBytes)
        {
            byte[] value = values[index++ % sourceCount];
            values.Add(value);
            total += value.Length;
        }
    }

    private static byte[] CreatePattern(int length, int seed)
    {
        var bytes = new byte[length];
        uint value = unchecked((uint)seed) | 1u;
        for (int index = 0; index < bytes.Length; index++)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            bytes[index] = (byte)value;
        }
        return bytes;
    }

    private static byte[][] CopyPieces(ReadOnlySpan<byte> source)
    {
        int count = (source.Length + SegmentBytes - 1) / SegmentBytes;
        var pieces = new byte[count][];
        for (int index = 0; index < pieces.Length; index++)
        {
            int offset = index * SegmentBytes;
            pieces[index] = source.Slice(offset, Math.Min(SegmentBytes, source.Length - offset)).ToArray();
        }
        return pieces;
    }

    private static void ApplyChange(Span<byte> bytes, int version)
        => ApplyChange(bytes, version, ChangeOffset(version));

    private static void ApplyChange(Span<byte> bytes, int version, int offset)
    {
        int length = Math.Min(LocalChangeBytes, bytes.Length - offset);
        bytes.Slice(offset, length).Fill(unchecked((byte)(0x40 + version)));
    }

    private static int ChangeOffset(int version)
        => checked((version - 1) * SegmentBytes + 8 * 1024);

    private static ulong HashArrays(IEnumerable<byte[]> values)
    {
        ulong hash = HashSeed;
        foreach (byte[] value in values)
        {
            hash = MixLength(hash, value.Length);
            hash = MixBytes(hash, value);
        }
        return hash;
    }

    private static ulong HashSegments(IEnumerable<SegmentValue> values, out int branches)
    {
        ulong hash = HashSeed;
        branches = 0;
        foreach (SegmentValue value in values)
        {
            hash = MixLength(hash, checked((int)value.Sequence.Length));
            foreach (ReadOnlyMemory<byte> piece in value.Sequence)
            {
                hash = MixBytes(hash, piece.Span);
                branches++;
            }
        }
        return hash;
    }

    private static unsafe ulong HashNative(IEnumerable<NativeSlice> values, out int branches)
    {
        ulong hash = HashSeed;
        branches = 0;
        foreach (NativeSlice value in values)
        {
            hash = MixLength(hash, value.Length);
            hash = MixBytes(hash, value.Bytes);
            branches++;
        }
        return hash;
    }

    private static ulong MixLength(ulong hash, int length)
        => BitOperations.RotateLeft(hash ^ unchecked((uint)length), 17) * HashMultiplier;

    private static ulong MixBytes(ulong hash, ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length >= sizeof(ulong))
        {
            hash = BitOperations.RotateLeft(hash ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes), 13) * HashMultiplier;
            bytes = bytes[sizeof(ulong)..];
        }
        foreach (byte value in bytes) hash = BitOperations.RotateLeft(hash ^ value, 7) * HashMultiplier;
        return hash;
    }

    private static int[] CollectionCounts() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private static string DescribeGc()
    {
        string[] markers = GC.GetConfigurationVariables().Keys
            .Where(static key => key.Contains("Satori", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        int largeObjectGeneration = GC.GetGeneration(new byte[100_000]);
        return markers.Length == 0
            ? $"large-object-generation={largeObjectGeneration.ToString(CultureInfo.InvariantCulture)}"
            : $"configuration={string.Join(',', markers)};large-object-generation={largeObjectGeneration.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static double[] Sorted(StorageReading[] values, Func<StorageReading, double> select)
        => values.Select(select).Order().ToArray();

    private static double Percentile(double[] sorted, double fraction)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];

    private static double Microseconds(double[] sorted, double fraction) => Percentile(sorted, fraction) / 1000d;

    private static double ElapsedNanoseconds(long started) => Stopwatch.GetElapsedTime(started).TotalNanoseconds;

    private sealed record Workload(string Name, string Source, IReadOnlyList<byte[]> Values, bool IsVersionCase);

    private readonly record struct StorageReading(
        double BuildNanoseconds,
        double ReadNanoseconds,
        double ReleaseNanoseconds,
        ulong Checksum,
        long InputBytes,
        long LiveValueBytes,
        long RetainedStorageBytes,
        long CopiedBytes,
        long NativeReservedBytes,
        long AlignmentWasteBytes,
        long AbandonedBytes,
        int PayloadObjectCount,
        int SegmentNodeCount,
        int TraversalBranches)
    {
        public double TotalNanoseconds => BuildNanoseconds + ReadNanoseconds + ReleaseNanoseconds;
    }

    private sealed class StorageResult
    {
        public int Schema { get; init; }
        public required string Kind { get; init; }
        public required string Case { get; init; }
        public required string Gc { get; init; }
        public required string GcEvidence { get; init; }
        public required string GcLatency { get; init; }
        public bool ServerGc { get; init; }
        public required string Framework { get; init; }
        public int ProcessorCount { get; init; }
        public int Samples { get; init; }
        public required string Source { get; init; }
        public int ValueCount { get; init; }
        public long InputBytes { get; init; }
        public long LiveValueBytes { get; init; }
        public long RetainedStorageBytes { get; init; }
        public long StorageAllocationBytesPerRound { get; init; }
        public long NativeReservedBytesPerRound { get; init; }
        public long AlignmentWasteBytesPerRound { get; init; }
        public long AbandonedBytesPerRound { get; init; }
        public long CopiedBytesPerRound { get; init; }
        public long ManagedAllocatedBytes { get; init; }
        public long NativeAllocatedBytesDuringSamples { get; init; }
        public int PayloadObjectCountPerRound { get; init; }
        public int SegmentNodeCountPerRound { get; init; }
        public int TraversalBranchesPerRound { get; init; }
        public required string Checksum { get; init; }
        public double BuildP50Microseconds { get; init; }
        public double BuildP95Microseconds { get; init; }
        public double BuildP99Microseconds { get; init; }
        public double ReadP50Microseconds { get; init; }
        public double ReadP95Microseconds { get; init; }
        public double ReadP99Microseconds { get; init; }
        public double ReleaseP50Microseconds { get; init; }
        public double ReleaseP95Microseconds { get; init; }
        public double ReleaseP99Microseconds { get; init; }
        public double EndToEndP50Microseconds { get; init; }
        public double EndToEndP95Microseconds { get; init; }
        public double EndToEndP99Microseconds { get; init; }
        public double EndToEndMaximumMicroseconds { get; init; }
        public double PayloadMegabytesPerSecondP50 { get; init; }
        public double CopyMegabytesPerSecondP50 { get; init; }
        public double HotElapsedMilliseconds { get; init; }
        public double ReclaimMilliseconds { get; init; }
        public double NativeFinalReleaseMilliseconds { get; init; }
        public double CombinedMilliseconds { get; init; }
        public double HotPauseMilliseconds { get; init; }
        public double ReclaimPauseMilliseconds { get; init; }
        public int Gen0Collections { get; init; }
        public int Gen1Collections { get; init; }
        public int Gen2Collections { get; init; }
        public int ReclaimGen0Collections { get; init; }
        public int ReclaimGen1Collections { get; init; }
        public int ReclaimGen2Collections { get; init; }
        public long ManagedBytesBefore { get; init; }
        public long ManagedBytesBeforeReclaim { get; init; }
        public long ManagedBytesAfterReclaim { get; init; }
        public long WorkingSetBefore { get; init; }
        public long WorkingSetBeforeReclaim { get; init; }
        public long WorkingSetAfterNativeRelease { get; init; }
        public long WorkingSetAfterReclaim { get; init; }
        public long PeakWorkingSet { get; init; }
        public double AllocationBytesPerCopiedByte { get; init; }
        public double RetentionAmplification { get; init; }
        public double StorageAllocationAmplification { get; init; }
        public double NativeReserveAmplification { get; init; }
    }

    private sealed class Options
    {
        public required string Kind { get; init; }
        public required string CaseName { get; init; }
        public required string Root { get; init; }
        public required string GcName { get; init; }
        public required string Latency { get; init; }
        public int Samples { get; init; }

        public static Options Parse(string[] args)
        {
            string? kind = null;
            string? caseName = null;
            string? root = null;
            string gcName = "CoreCLR";
            string latency = "interactive";
            int samples = 31;
            for (int index = 0; index < args.Length; index++)
            {
                if (index + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[index]}.");
                string value = args[++index];
                switch (args[index - 1])
                {
                    case "--kind": kind = value.ToLowerInvariant(); break;
                    case "--case": caseName = value.ToLowerInvariant(); break;
                    case "--root": root = Path.GetFullPath(value); break;
                    case "--gc": gcName = value; break;
                    case "--latency": latency = value.ToLowerInvariant(); break;
                    case "--samples" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count): samples = count; break;
                    default: throw new ArgumentException($"Unknown option {args[index - 1]}.");
                }
            }
            if (kind is not ("array" or "segments" or "native" or "native-reuse"))
                throw new ArgumentException("--kind must be array, segments, native, or native-reuse.");
            if (string.IsNullOrWhiteSpace(caseName)) throw new ArgumentException("--case is required.");
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("--root is required.");
            if (samples is < 3 or > 301) throw new ArgumentOutOfRangeException(nameof(samples), "--samples must be between 3 and 301.");
            if (latency is not ("interactive" or "low")) throw new ArgumentException("--latency must be interactive or low.");
            return new Options { Kind = kind, CaseName = caseName, Root = root, GcName = gcName, Latency = latency, Samples = samples };
        }
    }

    private sealed class SegmentValue
    {
        public ReadOnlySequence<byte> Sequence { get; }
        public int SegmentCount { get; }

        private SegmentValue(ReadOnlySequence<byte> sequence, int segmentCount)
        {
            Sequence = sequence;
            SegmentCount = segmentCount;
        }

        public static SegmentValue CopyFrom(byte[] source) => FromPieces(CopyPieces(source));

        public static SegmentValue FromPieces(byte[][] pieces)
        {
            if (pieces.Length == 0) return new SegmentValue(ReadOnlySequence<byte>.Empty, 0);
            PieceNode first = new(pieces[0]);
            PieceNode last = first;
            for (int index = 1; index < pieces.Length; index++) last = last.Append(pieces[index]);
            return new SegmentValue(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length), pieces.Length);
        }
    }

    private sealed class PieceNode : ReadOnlySequenceSegment<byte>
    {
        public PieceNode(byte[] bytes) => Memory = bytes;

        public PieceNode Append(byte[] bytes)
        {
            var next = new PieceNode(bytes) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private readonly unsafe struct NativeSlice
    {
        private readonly byte* address;
        public int Length { get; }
        public Span<byte> Bytes => new(address, Length);

        public NativeSlice(byte* address, int length)
        {
            this.address = address;
            Length = length;
        }
    }

    private sealed unsafe class NativeArena : IDisposable
    {
        private readonly List<NativeBlock> blocks = [];
        private int blockIndex;
        private bool disposed;

        public long ReservedBytes { get; private set; }
        public long AlignmentWasteBytes { get; private set; }
        public int BlockCount => blocks.Count;

        public NativeSlice CopyFrom(ReadOnlySpan<byte> source)
        {
            NativeSlice slice = Allocate(source.Length);
            source.CopyTo(slice.Bytes);
            return slice;
        }

        private NativeSlice Allocate(int length)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int required = Math.Max(length, 1);
            if (blocks.Count == 0) AddBlock(required);
            while (!blocks[blockIndex].CanAllocate(required))
            {
                blockIndex++;
                if (blockIndex == blocks.Count) AddBlock(required);
            }
            NativeBlock block = blocks[blockIndex];
            int aligned = checked((block.Used + 63) & ~63);
            AlignmentWasteBytes += aligned - block.Used;
            var slice = new NativeSlice(block.Address + aligned, length);
            block.Used = checked(aligned + required);
            blocks[blockIndex] = block;
            return slice;
        }

        private void AddBlock(int required)
        {
            int capacity = Math.Max(ArenaBlockBytes, checked((required + 63) & ~63));
            byte* address = (byte*)NativeMemory.AlignedAlloc((nuint)capacity, 64);
            if (address is null) throw new OutOfMemoryException();
            blocks.Add(new NativeBlock(address, capacity));
            ReservedBytes += capacity;
        }

        public void Reset()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            for (int index = 0; index < blocks.Count; index++)
            {
                NativeBlock block = blocks[index];
                block.Used = 0;
                blocks[index] = block;
            }
            blockIndex = 0;
            AlignmentWasteBytes = 0;
        }

        public void Dispose()
        {
            if (disposed) return;
            foreach (NativeBlock block in blocks) NativeMemory.AlignedFree(block.Address);
            blocks.Clear();
            ReservedBytes = 0;
            AlignmentWasteBytes = 0;
            blockIndex = 0;
            disposed = true;
        }

        private struct NativeBlock
        {
            public byte* Address;
            public int Capacity;
            public int Used;

            public NativeBlock(byte* address, int capacity)
            {
                Address = address;
                Capacity = capacity;
                Used = 0;
            }

            public readonly bool CanAllocate(int length)
            {
                int aligned = checked((Used + 63) & ~63);
                return aligned <= Capacity - length;
            }
        }
    }
}
