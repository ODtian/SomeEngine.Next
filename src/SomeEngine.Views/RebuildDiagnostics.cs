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
    internal static void Record<T>(World world, Entity view, bool firstBuild, uint parameterVersion) where T : struct
    {
        if (s_frames is { } frames && frames.TryGetValue(world, out var records))
            RecordSources(world, view, records, firstBuild, ComponentId<T>.Value, parameterVersion);
    }

    private static void RecordSources(World world, Entity view, List<RebuildRecord> records,
        bool firstBuild, ushort component, uint parameterVersion)
    {
        if (firstBuild)
        {
            records.Add(new RebuildRecord(view, Entity.Null, 0));
            return;
        }

        if (parameterVersion != world.Read<ParameterVersion>(view).Value)
            records.Add(new RebuildRecord(view, view, component));
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
                {
                    bool changed = dependency.Kind switch
                    {
                        DependencyKind.Environment => DependencyChanges.HasChanges(result.World, result.View, dependency),
                        DependencyKind.Presence => result.World.HasComponentData(dependency.Entity, dependency.Component) != (dependency.Version != 0),
                        DependencyKind.Membership => result.World.MembershipVersion(dependency.Query) != dependency.Version,
                        _ => !result.World.HasComponentData(dependency.Entity, dependency.Component) ||
                            result.World.ComponentDataWriteVersion(dependency.Entity, dependency.Component) != dependency.Version,
                    };
                    if (changed)
                        result.Records.Add(new RebuildRecord(result.View, dependency.Entity, dependency.Component) { Query = dependency.Query });
                }
            });
    }
}

/// <summary>A rebuilt view and the entity component that caused its rebuild.</summary>
/// <remarks>First builds use Entity.Null as Source and zero as Component. Parameter changes use the view as Source.</remarks>
public readonly record struct RebuildRecord(Entity View, Entity Source, ushort Component)
{
    /// <summary>The query whose membership changed, or an invalid handle for entity sources and first builds.</summary>
    public QueryHandle Query { get; init; }
}
