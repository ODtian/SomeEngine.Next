using System.Runtime.InteropServices;

namespace SomeEngine.Diagnostics;

public enum TraceEventKind { Begin, End, Frame, Counter }

// Little-endian storage, fixed 56-byte records. Parent is execution nesting; Related is causality.
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TraceEvent(
    long Timestamp, long Id, long Parent, long Related, long Frame, long Value, int Site, TraceEventKind Kind);

public sealed record TraceOptions
{
    public int BufferMegabytes { get; init; } = 64;
    public bool Detail { get; init; }
    public bool Scopes { get; init; } = true;
    public bool Allocations { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
