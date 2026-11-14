using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Comparison;

// Fixture entities of the SomeEngine comparison paths. Every reference library creates the same component sets.

internal partial struct Motion : IComponentBundle
{
    internal Position Position;
    internal Velocity Velocity;
}

internal partial struct Body : IComponentBundle
{
    internal Position Position;
    internal Velocity Velocity;
    internal Health Health;

    /// <summary>The moving body used by the standard workloads.</summary>
    internal static Body Moving(in Position position)
        => new() { Position = position, Velocity = new() { X = 1, Y = 2 }, Health = new() { Value = 100, Max = 100 } };
}

internal partial struct TeamBody : IComponentBundle { internal Body Body; internal Team Team; }
internal partial struct SleepingTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal Sleeping Sleeping; }
internal partial struct GrowthBody : IComponentBundle { internal Body Body; internal GrowthMarker Marker; }
internal partial struct ExtraBody : IComponentBundle { internal Body Body; internal Extra Extra; }

internal partial struct SignalBody : IComponentBundle { internal Body Body; internal Signal Signal; }
internal partial struct SignalTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal Signal Signal; }
internal partial struct SignalSleepingTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal Sleeping Sleeping; internal Signal Signal; }

internal partial struct OrdinalBody : IComponentBundle { internal Body Body; internal JobOrdinal Ordinal; }
internal partial struct OrdinalTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal JobOrdinal Ordinal; }
internal partial struct OrdinalSleepingTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal Sleeping Sleeping; internal JobOrdinal Ordinal; }

internal partial struct RandomWriteBody : IComponentBundle { internal Body Body; internal JobOrdinal Ordinal; internal JobExtra Extra; }
internal partial struct RandomWriteTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal JobOrdinal Ordinal; internal JobExtra Extra; }
internal partial struct RandomWriteSleepingTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal Sleeping Sleeping; internal JobOrdinal Ordinal; internal JobExtra Extra; }

internal partial struct BufferBody : IComponentBundle { internal Body Body; internal ReadOnlyMemory<BufferItem> Items; }
internal partial struct BufferTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal ReadOnlyMemory<BufferItem> Items; }
internal partial struct BufferSleepingTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal Sleeping Sleeping; internal ReadOnlyMemory<BufferItem> Items; }

internal partial struct ScoreBody : IComponentBundle { internal Body Body; internal ScoreValue Score; }
internal partial struct ScoreTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal ScoreValue Score; }
internal partial struct ScoreSleepingTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal Sleeping Sleeping; internal ScoreValue Score; }

internal partial struct IndexedBody : IComponentBundle { internal Body Body; internal IndexedNumber Number; }
internal partial struct IndexedTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal IndexedNumber Number; }
internal partial struct IndexedSleepingTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal Sleeping Sleeping; internal IndexedNumber Number; }

internal partial struct SharedBody : IComponentBundle { internal Body Body; internal SharedNumber Number; }
internal partial struct SharedTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal SharedNumber Number; }
internal partial struct SharedSleepingTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal Sleeping Sleeping; internal SharedNumber Number; }

internal partial struct CopyBody : IComponentBundle
{ internal Body Body; internal SomeCopyNode Node; internal IndexedNumber Number; internal ReadOnlyMemory<SomeCopyItem> Items; }
internal partial struct CopyTeamBody : IComponentBundle
{ internal Body Body; internal Team Team; internal SomeCopyNode Node; internal IndexedNumber Number; internal ReadOnlyMemory<SomeCopyItem> Items; }
internal partial struct CopySleepingTeamBody : IComponentBundle
{
    internal Body Body; internal Team Team; internal Sleeping Sleeping;
    internal SomeCopyNode Node; internal IndexedNumber Number; internal ReadOnlyMemory<SomeCopyItem> Items;
}

internal partial struct ArchiveBody : IComponentBundle
{
    internal Body Body; internal SaveCounter Counter; internal SaveStep Step;
    internal ReadOnlyMemory<SaveLink> Links; internal IndexedNumber Number; internal SaveShared Shared;
}
internal partial struct ArchiveTeamBody : IComponentBundle { internal ArchiveBody Archive; internal Team Team; }
internal partial struct ArchiveSleepingTeamBody : IComponentBundle { internal ArchiveBody Archive; internal Team Team; internal Sleeping Sleeping; }
internal partial struct ExtraTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal Extra Extra; }
internal partial struct ExtraSleepingTeamBody : IComponentBundle { internal Body Body; internal Team Team; internal Sleeping Sleeping; internal Extra Extra; }
