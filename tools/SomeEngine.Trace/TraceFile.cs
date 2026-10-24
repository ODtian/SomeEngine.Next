using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SomeEngine.Diagnostics;

namespace SomeEngine.Trace;

public sealed record TraceSiteInfo(int Id, string Category, string Name, bool Detail);
public sealed record TraceThreadInfo(int Id, int NativeId, string Name, long Dropped);
public readonly record struct ThreadEvent(int Thread, TraceEvent Event);
public sealed record TraceData(long Frequency, long Start, long Stop, long UtcTicks, int ProcessId,
    bool Detail, bool Allocations, Dictionary<string, string> Metadata,
    TraceSiteInfo[] Sites, TraceThreadInfo[] Threads, ThreadEvent[] Events, long Misuse);

/// <summary>Raw format decoding only. No performance thresholds or engine-specific policy.</summary>
public static class TraceFile
{
    public static TraceData Read(string path)
    {
        using var file = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (!file.ReadBytes(8).AsSpan().SequenceEqual("SETRACE1"u8) || file.ReadInt32() != 2)
            throw new InvalidDataException("Unsupported trace format.");
        long frequency = file.ReadInt64(), start = file.ReadInt64(), utc = file.ReadInt64();
        int process = file.ReadInt32();
        bool detail = file.ReadBoolean(), allocations = file.ReadBoolean();
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(file.ReadString()) ?? [];
        if (frequency <= 0) throw new InvalidDataException("Invalid trace clock.");
        List<ThreadEvent> events = [];
        while (true)
        {
            byte kind = file.ReadByte();
            if (kind == 1)
            {
                int thread = file.ReadInt32(), count = file.ReadInt32();
                if (thread <= 0 || count is < 1 or > 4096) throw new InvalidDataException("Invalid trace block.");
                byte[] bytes = file.ReadBytes(checked(count * 56));
                if (bytes.Length != count * 56) throw new EndOfStreamException("Truncated trace block.");
                foreach (TraceEvent ev in MemoryMarshal.Cast<byte, TraceEvent>(bytes))
                    events.Add(new ThreadEvent(thread, ev));
                continue;
            }
            if (kind != 2) throw new InvalidDataException("Invalid trace record.");
            long stop = file.ReadInt64(), misuse = file.ReadInt64();
            int siteCount = ReadCount(file);
            TraceSiteInfo[] sites = new TraceSiteInfo[siteCount];
            for (int i = 0; i < sites.Length; i++)
                sites[i] = new(file.ReadInt32(), file.ReadString(), file.ReadString(), file.ReadBoolean());
            int threadCount = ReadCount(file);
            TraceThreadInfo[] threads = new TraceThreadInfo[threadCount];
            for (int i = 0; i < threads.Length; i++)
                threads[i] = new(file.ReadInt32(), file.ReadInt32(), file.ReadString(), file.ReadInt64());
            long expected = file.ReadInt64();
            if (expected != events.Count || file.BaseStream.Position != file.BaseStream.Length || stop < start)
                throw new InvalidDataException("Trace completion record does not match the data.");
            return new TraceData(frequency, start, stop, utc, process, detail, allocations, metadata,
                sites, threads, [.. events], misuse);
        }
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 8)
            throw new InvalidDataException("Invalid trace metadata count.");
        return count;
    }
}
