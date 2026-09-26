using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

internal static class PageMeasurements
{
    internal static void Run()
    {
        foreach (int pageSize in new[] { 4096, 16384, 65536 })
        {
            using var arena = CreateArena(pageSize);
            foreach (int length in new[] { 128, 8192, 2 * 1024 * 1024, 16 * 1024 * 1024 })
                MeasureBranches(arena, length);
            MeasureReadsAndWrites(arena);
            MeasureSmallRead(arena);
        }
        MeasureOrdinaryElements();
    }

    private static PageArena CreateArena(int pageSize) => new(
        new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = 256L * 1024 * 1024 }),
        new() { MinimumPageSize = 256, MaximumPageSize = pageSize, AllocationSize = 256 * 1024 });

    private static void MeasureBranches(PageArena arena, int length)
    {
        using var bytes = new PageBuffer(arena, length);
        bytes.Write(0, length, static (_, page) => page.Fill(31));
        for (int i = 0; i < 10; i++) { using var warmup = bytes.Duplicate(); warmup[0] = 19; }
        double[] duplicate = new double[101], write = new double[101];
        long duplicateAllocation = 0, writeAllocation = 0;
        long copied = arena.Statistics.CopiedBytes;
        for (int i = 0; i < duplicate.Length; i++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            using var branch = bytes.Duplicate();
            duplicate[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            duplicateAllocation += GC.GetAllocatedBytesForCurrentThread() - allocated;
            allocated = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            branch[(long)i * 7919 % length] = 27;
            write[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            writeAllocation += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        copied = (arena.Statistics.CopiedBytes - copied) / duplicate.Length;
        Array.Sort(duplicate); Array.Sort(write);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"page-branches: page={arena.MaximumPageSize} bytes={length} duplicate-p50={duplicate[50]:F0}ns duplicate-p95={duplicate[95]:F0}ns duplicate-allocation={duplicateAllocation / 101} first-write-p50={write[50]:F0}ns first-write-p95={write[95]:F0}ns first-write-allocation={writeAllocation / 101} copied={copied}"));
    }

    private static void MeasureReadsAndWrites(PageArena arena)
    {
        const int length = 2 * 1024 * 1024;
        using var bytes = new PageBuffer(arena, length);
        byte[] destination = new byte[length];
        double[] read = new double[31], write = new double[31], random = new double[31];
        long checksum = 0;
        for (int i = 0; i < read.Length; i++)
        {
            using var branch = bytes.Duplicate();
            long start = Stopwatch.GetTimestamp();
            branch.Write(0, length, static (_, page) => page.Fill(31));
            write[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            start = Stopwatch.GetTimestamp();
            branch.Read(0, destination);
            read[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            start = Stopwatch.GetTimestamp();
            for (int j = 0; j < 10000; j++) checksum += branch.Read<int>((long)j * 199919 % (length - 4));
            random[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / 10000;
        }
        Array.Sort(read); Array.Sort(write); Array.Sort(random);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"page-access: page={arena.MaximumPageSize} bytes={length} copy-p50={read[15]:F2}us copy-p95={read[29]:F2}us batch-cow-p50={write[15]:F2}us batch-cow-p95={write[29]:F2}us random-p50={random[15]:F2}ns random-p95={random[29]:F2}ns checksum={checksum}"));
    }

    private static void MeasureSmallRead(PageArena arena)
    {
        arena.Trim();
        var bytes = new PageBuffer(arena, 2 * 1024 * 1024);
        using var read = bytes.Retain(157, 1);
        bytes.Dispose();
        arena.Trim();
        PageArenaStatistics statistics = arena.Statistics;
        Console.WriteLine($"page-small-read: page={arena.MaximumPageSize} requested=1 active={statistics.ActiveBytes} allocated={statistics.AllocatedBytes} idle={statistics.IdleBytes}");
        read.Dispose();
        arena.Trim();
        if (arena.Statistics.ActiveBytes != 0 || arena.Statistics.AllocatedBytes != 0)
            throw new InvalidOperationException("Released measurement content retained page storage.");
    }

    private static void MeasureOrdinaryElements()
    {
        using var arena = CreateArena(16384);
        using var points = new PageList<Point>(arena);
        for (int i = 0; i < 65536; i++) points.Add(new(i, i + 1, i + 2));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        float checksum = 0;
        foreach (Point point in points) checksum += point.X;
        double nanoseconds = Stopwatch.GetElapsedTime(start).TotalNanoseconds / points.Count;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"page-elements: count={points.Count} stride=12 read={nanoseconds:F2}ns allocated={allocated} active={arena.Statistics.ActiveBytes} checksum={checksum}"));
    }

    private readonly record struct Point(float X, float Y, float Z);
}
