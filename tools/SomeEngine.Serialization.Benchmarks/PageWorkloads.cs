using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

/// <summary>
/// Measures complete public-API workloads, including their retention and scheduling costs.
/// File reads use the operating system's normal cache; these are not cold-disk measurements.
/// </summary>
internal static class PageWorkloads
{
    private const int AllocationBytes = 256 * 1024;
    private const int FileBytes = 16 * 1024 * 1024;
    private const byte FileValue = 71;

    internal static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"someengine-page-workloads-{Guid.NewGuid():N}.bin");
        try
        {
            await WriteInputAsync(path, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("page-workloads: minimum-page=256 allocation=262144 retained-empty-per-size=1 file-cache=uncontrolled");
            foreach (int pageBytes in new[] { 4096, 16384, 65536 })
            {
                MeasureMixedLifetimes(pageBytes, cancellationToken);
                await MeasureTransfersAsync(path, pageBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static PageArena CreateArena(int pageBytes) => new(
        new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = 128L * 1024 * 1024 }),
        new PageArenaOptions
        {
            MinimumPageSize = 256,
            MaximumPageSize = pageBytes,
            AllocationSize = AllocationBytes,
            RetainedEmptyAllocationsPerSize = 1,
        });

    private static void MeasureMixedLifetimes(int pageBytes, CancellationToken cancellationToken)
    {
        const int rounds = 257;
        const int sourceBytes = 8 * 1024 * 1024;
        const int editBytes = 96;
        const byte sourceValue = 31;
        int[] lengths = [128, 8192, 512 * 1024, 2 * 1024 * 1024];
        using var arena = CreateArena(pageBytes);
        var longReads = new List<(PageRange<byte> Bytes, byte Value)>(16);
        var recentReads = new Queue<(PageRange<byte> Bytes, byte Value)>(25);
        double[] durations = new double[rounds];
        long peakActive = 0, peakBacking = 0, allocation = 0, copied = 0;
        long retainedRequested = 0;
        PageArenaStatistics retained = default;
        Span<byte> edit = stackalloc byte[editBytes];
        try
        {
            using (var source = new PageBuffer(arena, sourceBytes))
            {
                source.Write(0, sourceBytes, static (_, bytes) => bytes.Fill(sourceValue));
                for (int index = 0; index < 8; index++)
                {
                    using var branch = source.Duplicate();
                    branch[index] = 17;
                    using var read = branch.Retain(index, 1);
                    CheckBytes(read, 17);
                }

                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long copiedBefore = arena.Statistics.CopiedBytes;
                for (int round = 0; round < rounds; round++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long start = Stopwatch.GetTimestamp();
                    byte value = (byte)(32 + round % 192);
                    long offset = ((long)round * 7919 + pageBytes - 17) % (sourceBytes - editBytes);
                    using (var branch = source.Duplicate())
                    using (var temporary = new PageBuffer(arena, lengths[round % lengths.Length]))
                    {
                        edit.Fill(value);
                        branch.Write(offset, edit);
                        recentReads.Enqueue((branch.Retain(offset, editBytes), value));

                        // The retained read must continue to see the preceding bytes.
                        branch[offset] = (byte)(value ^ 0xFF);
                        if (source[offset] != sourceValue)
                            throw new InvalidOperationException("A branch edit changed the shared source.");

                        temporary.Write(0, temporary.Length, static (_, bytes) => bytes.Fill(FileValue));
                        if (round % 17 == 0)
                            longReads.Add((temporary.Retain((temporary.Length - 32) / 2, 32), FileValue));

                        if (recentReads.Count > 24)
                        {
                            var oldest = recentReads.Dequeue();
                            using (oldest.Bytes) CheckBytes(oldest.Bytes, oldest.Value);
                        }

                        PageArenaStatistics usage = arena.Statistics;
                        peakActive = Math.Max(peakActive, usage.ActiveBytes);
                        peakBacking = Math.Max(peakBacking, usage.BackingBytes);
                    }
                    // Round timing includes edits, content checks, retention, bookkeeping and disposal.
                    durations[round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
                allocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                copied = arena.Statistics.CopiedBytes - copiedBefore;
            }

            arena.Trim();
            retained = arena.Statistics;
            foreach (var read in longReads)
            {
                CheckBytes(read.Bytes, read.Value);
                retainedRequested += read.Bytes.Sequence.Length;
            }
            foreach (var read in recentReads)
            {
                CheckBytes(read.Bytes, read.Value);
                retainedRequested += read.Bytes.Sequence.Length;
            }
        }
        finally
        {
            foreach (var read in longReads) read.Bytes.Dispose();
            foreach (var read in recentReads) read.Bytes.Dispose();
        }

        RequireEmpty(arena);
        Array.Sort(durations);
        long edited = (long)rounds * (editBytes + 1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"page-mixed: page={pageBytes} rounds={rounds} source-bytes={sourceBytes} round-p50={Percentile(durations, 0.50):F2}us round-p95={Percentile(durations, 0.95):F2}us round-p99={Percentile(durations, 0.99):F2}us allocation={allocation} copied={copied} edited={edited} copy-ratio={(double)copied / edited:F2} peak-active={peakActive} peak-backing={peakBacking} held-requested={retainedRequested} held-active={retained.ActiveBytes} held-backing={retained.BackingBytes} after-release=0"));
    }

    private static async Task MeasureTransfersAsync(string path, int pageBytes, CancellationToken cancellationToken)
    {
        const int workers = 8;
        const int readsPerWorker = 16;
        const int readBytes = 256 * 1024;
        using var arena = CreateArena(pageBytes);
        await using var source = FileRangeSource.Open(path);
        using (var first = await PageBuffer.ReadAsync(source, 0, readBytes, arena, cancellationToken).ConfigureAwait(false))
        using (var read = first.Retain(0, 64))
            CheckBytes(read, FileValue);

        double[] durations = new double[workers * readsPerWorker];
        long peakActive = 0, peakBacking = 0;
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        Task[] tasks = new Task[workers];
        for (int worker = 0; worker < workers; worker++)
        {
            int index = worker;
            tasks[worker] = Task.Run(async () =>
            {
                for (int readIndex = 0; readIndex < readsPerWorker; readIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int sample = index * readsPerWorker + readIndex;
                    long offset = (long)(sample * 7919) % (FileBytes - readBytes);
                    long start = Stopwatch.GetTimestamp();
                    PageRange<byte> read;
                    using (var bytes = await PageBuffer.ReadAsync(source, offset, readBytes, arena, cancellationToken).ConfigureAwait(false))
                    {
                        PageArenaStatistics usage = arena.Statistics;
                        KeepMaximum(ref peakActive, usage.ActiveBytes);
                        KeepMaximum(ref peakBacking, usage.BackingBytes);
                        read = bytes.Retain(readBytes - 64, 64);
                    }
                    using (read)
                    {
                        // The complete input buffer is closed before the continuation runs.
                        await Task.Yield();
                        CheckBytes(read, FileValue);
                    }
                    durations[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
            });
        }

        // Every worker is joined, including on failure, before the file or arena is closed.
        await Task.WhenAll(tasks).ConfigureAwait(false);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        long allocation = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        RequireEmpty(arena);
        Array.Sort(durations);
        long transferred = (long)durations.Length * readBytes;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"page-transfer: page={pageBytes} workers={workers} reads={durations.Length} bytes-per-read={readBytes} read-retain-yield-p50={Percentile(durations, 0.50):F2}us read-retain-yield-p95={Percentile(durations, 0.95):F2}us read-retain-yield-p99={Percentile(durations, 0.99):F2}us throughput={transferred / (1024.0 * 1024.0) / elapsed:F2}MiB/s process-allocation={allocation} peak-active={peakActive} peak-backing={peakBacking} after-release=0"));
    }

    private static async Task WriteInputAsync(string path, CancellationToken cancellationToken)
    {
        const int chunkBytes = 64 * 1024;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(chunkBytes);
        try
        {
            bytes.AsSpan(0, chunkBytes).Fill(FileValue);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                chunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (int offset = 0; offset < FileBytes; offset += chunkBytes)
                await stream.WriteAsync(bytes.AsMemory(0, Math.Min(chunkBytes, FileBytes - offset)), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static void CheckBytes(PageRange<byte> read, byte expected)
    {
        foreach (ReadOnlyMemory<byte> memory in read.Sequence)
            foreach (byte value in memory.Span)
                if (value != expected)
                    throw new InvalidOperationException("Retained page content changed during the workload.");
    }

    private static void RequireEmpty(PageArena arena)
    {
        arena.Trim();
        PageArenaStatistics usage = arena.Statistics;
        if (usage.ActiveBytes != 0 || usage.AllocatedBytes != 0 || usage.BackingBytes != 0)
            throw new InvalidOperationException("The completed workload still retains page storage.");
    }

    private static double Percentile(double[] sorted, double fraction)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];

    private static void KeepMaximum(ref long destination, long value)
    {
        long previous = Volatile.Read(ref destination);
        while (value > previous)
        {
            long observed = Interlocked.CompareExchange(ref destination, value, previous);
            if (observed == previous) return;
            previous = observed;
        }
    }
}
