using System.Diagnostics;

namespace SomeEngine.ECS;

/// <summary>Optional cumulative times for actual structure writes, command playback and query membership facts.</summary>
public sealed class WorldStructureTimings
{
    [ThreadStatic] private static WorldStructureTimings? s_current;
    [ThreadStatic] private static int s_kinds;
    private long _structureTicks, _playbackTicks, _membershipTicks;
    private long _structureCount, _playbackCount, _membershipCount;

    public bool Enabled { get; set; }
    public WorldStructureTimes Read() => new(Interlocked.Read(ref _structureTicks), Interlocked.Read(ref _playbackTicks),
        Interlocked.Read(ref _membershipTicks), Interlocked.Read(ref _structureCount),
        Interlocked.Read(ref _playbackCount), Interlocked.Read(ref _membershipCount));

    internal Measurement Measure(WorldTimingKind kind)
    {
        if (!Enabled) return default;
        WorldStructureTimings? previous = s_current;
        int kinds = s_kinds;
        bool nested = ReferenceEquals(previous, this) && (kinds & (int)kind) != 0;
        s_current = this;
        s_kinds = (ReferenceEquals(previous, this) ? kinds : 0) | (int)kind;
        return new(this, kind, nested ? 0 : Stopwatch.GetTimestamp(), previous, kinds);
    }

    internal readonly struct Measurement(WorldStructureTimings? data, WorldTimingKind kind, long started,
        WorldStructureTimings? previous, int previousKinds) : IDisposable
    {
        public void Dispose()
        {
            if (data is null) return;
            if (started != 0)
            {
                long ticks = Stopwatch.GetTimestamp() - started;
                switch (kind)
                {
                    case WorldTimingKind.StructureWrite:
                        Interlocked.Add(ref data._structureTicks, ticks); Interlocked.Increment(ref data._structureCount); break;
                    case WorldTimingKind.CommandPlayback:
                        Interlocked.Add(ref data._playbackTicks, ticks); Interlocked.Increment(ref data._playbackCount); break;
                    case WorldTimingKind.MembershipChanges:
                        Interlocked.Add(ref data._membershipTicks, ticks); Interlocked.Increment(ref data._membershipCount); break;
                }
            }
            s_current = previous; s_kinds = previousKinds;
        }
    }
}

/// <summary>Independent cumulative scopes; structure time includes the membership work performed inside it.</summary>
public readonly record struct WorldStructureTimes(long StructureWriteTicks, long CommandPlaybackTicks,
    long MembershipChangesTicks, long StructureWriteCount, long CommandPlaybackCount, long MembershipChangeBatches)
{
    public double StructureWriteMilliseconds => StructureWriteTicks * (1000d / Stopwatch.Frequency);
    public double CommandPlaybackMilliseconds => CommandPlaybackTicks * (1000d / Stopwatch.Frequency);
    public double MembershipChangesMilliseconds => MembershipChangesTicks * (1000d / Stopwatch.Frequency);
}

internal enum WorldTimingKind : byte { StructureWrite = 1, CommandPlayback = 2, MembershipChanges = 4 }

public sealed partial class World
{
    private WorldStructureTimings? _structureTimings;
    public WorldStructureTimings StructureTimings => _structureTimings ??= new();
    internal WorldStructureTimings.Measurement MeasureStructure(WorldTimingKind kind) => _structureTimings?.Measure(kind) ?? default;
}
