using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Views;

/// <summary>Changed sources for view builds during each World's most recent rebuild phase.</summary>
public static class RebuildDiagnostics
{
    private static ConditionalWeakTable<World, List<RebuildRecord>>? s_frames;

    /// <summary>Enables collection for all Worlds. The default is false.</summary>
    /// <remarks>Change this setting between rebuild phases. Disabling releases the collected frames.</remarks>
    public static bool Enabled
    {
        get => s_frames is not null;
        set => s_frames = value ? s_frames ?? new() : null;
    }

    /// <summary>Reads the World's most recently collected rebuild phase.</summary>
    /// <remarks>Read after the rebuild phase and consume the span before the World's next phase or a setting change.</remarks>
    public static ReadOnlySpan<RebuildRecord> LastFrame(World world)
    {
        DevelopmentCheck.Require(world is not null, "Rebuild diagnostics require a World.");
        return s_frames is { } frames && frames.TryGetValue(world, out var records)
            ? CollectionsMarshal.AsSpan(records) : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BeginFrame(World world)
    {
        if (s_frames is { } frames)
            ClearFrame(frames, world);
    }

    private static void ClearFrame(ConditionalWeakTable<World, List<RebuildRecord>> frames, World world) =>
        frames.GetValue(world, static _ => new()).Clear();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Record<T>(World world, Entity view, ulong frame, bool firstBuild, uint parameterVersion) where T : struct, IView
    {
        if (s_frames is { } frames && frames.TryGetValue(world, out var records))
            RecordSources(world, view, frame, records, firstBuild, T.Mode == BuildMode.Rerun, ComponentId<T>.Value, parameterVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Record(Computation computation, ulong frame)
    {
        if (s_frames is not { } frames || !frames.TryGetValue(computation.World, out var records))
            return;
        foreach (Dependency read in computation.Reads)
            RecordDependency(computation.World, computation.Entity, records, read);
        RecordObjects(computation.ObjectReads, computation.Entity, frame, records);
    }

    private static void RecordSources(World world, Entity view, ulong frame, List<RebuildRecord> records,
        bool firstBuild, bool rerun, ushort component, uint parameterVersion)
    {
        if (firstBuild)
        {
            records.Add(new RebuildRecord(view, Entity.Null, 0));
            return;
        }

        if (rerun && parameterVersion != world.Read<ParameterVersion>(view).Value)
            records.Add(new RebuildRecord(view, view, component));
        if (world.Has<ObjectDependencies>(view))
            RecordObjects(world.Read<ObjectDependencies>(view).Reads, view, frame, records);
        RecordDependencies(world, view, records);
    }

    private static void RecordDependencies(World world, Entity view, List<RebuildRecord> records)
    {
        if (!world.HasBuffer<Dependency>(view))
            return;

        var changes = (World: world, View: view, Records: records);
        world.ExecuteBufferRead<Dependency, (World World, Entity View, List<RebuildRecord> Records)>(
            view, ref changes, static (buffer, ref result) =>
            {
                foreach (Dependency dependency in buffer.AsSpan())
                    RecordDependency(result.World, result.View, result.Records, dependency);
            });
    }

    private static void RecordDependency(World world, Entity view, List<RebuildRecord> records, Dependency read)
    {
        if (DependencyChanges.HasChanges(world, view, read))
            records.Add(new RebuildRecord(view, read.Entity, read.Component) { Query = read.Query });
    }

    private static void RecordObjects(ObjectDependencyList reads, Entity view, ulong frame, List<RebuildRecord> records)
    {
        foreach (var read in reads.Reads)
            if (reads.HasChanges(read, frame))
                records.Add(new RebuildRecord(view, Entity.Null, 0) { SourceObject = read.Cell });
    }
}

/// <summary>A rebuilt view and the entity component that caused its rebuild.</summary>
/// <remarks>First builds use Entity.Null as Source and zero as Component. Parameter changes use the view as Source.</remarks>
public readonly record struct RebuildRecord(Entity View, Entity Source, ushort Component)
{
    /// <summary>The query whose membership changed, or an invalid handle for entity sources and first builds.</summary>
    public QueryHandle Query { get; init; }

    /// <summary>The State, Memo, or version cell whose observed data changed.</summary>
    public VersionCell? SourceObject { get; init; }
}
