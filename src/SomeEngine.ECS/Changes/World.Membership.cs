using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS;

public enum MembershipChangeKind : byte { Added, Removed }

public readonly record struct MembershipChange(Entity Entity, MembershipChangeKind Kind, ulong Sequence);
internal readonly record struct QueryMembershipData(QueryDescription Description, ulong Version,
    EventLogData<MembershipChange>? Events);

internal readonly record struct MembershipMatch(QueryData Query, QueryRowFilter? Filter);

internal readonly struct MembershipEdit(World? world, int index) : IDisposable
{
    public void Dispose() => world?.FinishMembershipEdit(index);
}

public sealed partial class World
{
    private readonly List<QueryData> _membershipQueries = [];
    private int _membershipCacheVersion;
    private MembershipEditData[] _membershipEdits = [];
    private int _membershipEditCount;

    private struct MembershipEditData
    {
        internal Entity Entity;
        internal MembershipMatch[] Matches;
        internal ulong Bits;
        internal ulong[]? Words;
    }

    /// <summary>Observes entries and exits published after this registration.</summary>
    public EventReader<MembershipChange> WatchMembership(Query query)
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        QueryData data = query.Data;
        if (data.MembershipLog is null)
        {
            data.MembershipLog = new(_eventScheduler);
            if (Execution is not null) Execution.RegisterEventAccess(data.MembershipLog.Write);
            TrackMembership(data);
        }
        return data.MembershipLog.Watch();
    }

    internal QueryMembershipData[] CopyMembershipData(QueryMembershipData[]? target = null)
    {
        if (target is null || target.Length != _membershipQueries.Count) target = new QueryMembershipData[_membershipQueries.Count];
        for (int index = 0; index < target.Length; index++)
        {
            QueryData data = _membershipQueries[index];
            EventLogData<MembershipChange>? events = data.MembershipLog?.CaptureData(target[index].Events,
                Execution?.CurrentAccess ?? default);
            target[index] = new(data.Description, data.MembershipVersion, events);
        }
        return target;
    }

    internal void RestoreMembershipData(ReadOnlySpan<QueryMembershipData> saved)
    {
        foreach (QueryMembershipData value in saved)
        {
            QueryData data = BindQuery(value.Description.Definition);
            TrackMembership(data);
            data.MembershipVersion = value.Version;
            if (value.Events is not { } events) continue;
            if (data.MembershipLog is null)
            {
                data.MembershipLog = new(_eventScheduler);
                if (Execution is not null) Execution.RegisterEventAccess(data.MembershipLog.Write);
            }
            data.MembershipLog.RestoreData(events, Execution?.CurrentAccess ?? default, restoreAnonymousReaders: true);
        }
    }

    internal ulong ReadMembershipVersion(QueryData data)
    {
        if (!Volatile.Read(ref data.TracksMembership))
        {
            using WorldAccess access = OpenStructure(false);
            lock (LazyInitializer.EnsureInitialized(ref _queryBindingsLock)) TrackMembership(data);
        }
        return data.MembershipVersion;
    }

    private void TrackMembership(QueryData data)
    {
        if (data.TracksMembership) return;
        data.MembershipVersion = Version;
        _membershipQueries.Add(data);
        _membershipCacheVersion++;
        Volatile.Write(ref data.TracksMembership, true);
    }

    private MembershipMatch[] MembershipMatches(Archetype archetype)
    {
        if (archetype.MembershipCacheVersion == _membershipCacheVersion) return archetype.MembershipQueries;
        var matches = new List<MembershipMatch>();
        foreach (QueryData query in _membershipQueries)
        {
            QueryDescription description = query.Description;
            if (archetype.Types.Bits.Contains(description.Required)
                && !archetype.Types.Bits.IntersectsExcept(description.Excluded, archetype.Optional)
                && description.MatchesGroups(archetype)
                && (description.Any.Count == 0 || archetype.Types.Bits.Intersects(description.Any)))
                matches.Add(new(query, QueryRowFilter.Create(archetype, description)));
        }
        archetype.MembershipQueries = matches.ToArray();
        archetype.MembershipCacheVersion = _membershipCacheVersion;
        return archetype.MembershipQueries;
    }

    internal MembershipEdit BeginMembershipEdit(Entity entity, bool created = false)
    {
        if (_membershipQueries.Count == 0 || !created && !IsAliveCore(entity)) return default;
        for (int index = 0; index < _membershipEditCount; index++)
            if (_membershipEdits[index].Entity == entity) return default;
        MembershipMatch[] matches = created ? [] : MembershipMatches(Records[entity.Id].Chunk!.Archetype);
        var state = new MembershipEditData { Entity = entity, Matches = matches };
        if (matches.Length > 64)
        {
            state.Words = ArrayPool<ulong>.Shared.Rent((matches.Length + 63) >> 6);
            state.Words.AsSpan(0, (matches.Length + 63) >> 6).Clear();
        }
        if (!created)
        {
            ref EntityRecord record = ref Records[entity.Id];
            for (int index = 0; index < matches.Length; index++)
                if (MatchesMembership(matches[index], record.Chunk!, record.Row))
                {
                    if (state.Words is { } words) words[index >> 6] |= 1UL << (index & 63);
                    else state.Bits |= 1UL << index;
                }
        }
        if (_membershipEditCount == _membershipEdits.Length)
            Array.Resize(ref _membershipEdits, Math.Max(4, _membershipEditCount * 2));
        int position = _membershipEditCount++;
        _membershipEdits[position] = state;
        return new(this, position);
    }

    private static bool MatchesMembership(in MembershipMatch match, Chunk chunk, int row)
        => match.Filter is null || match.Filter.Contains(chunk, row, 0);

    internal void FinishMembershipEdit(int position)
    {
        MembershipEditData state = _membershipEdits[position];
        _membershipEdits[position] = default;
        _membershipEditCount--;
        try
        {
            bool alive = IsAliveCore(state.Entity);
            MembershipMatch[] after = alive ? MembershipMatches(Records[state.Entity.Id].Chunk!.Archetype) : [];
            ref EntityRecord record = ref Records[state.Entity.Id];
            for (int index = 0; index < state.Matches.Length; index++)
            {
                QueryData query = state.Matches[index].Query;
                bool before = ((state.Words is { } words ? words[index >> 6] : state.Bits) & (1UL << (index & 63))) != 0;
                bool contains = false;
                foreach (MembershipMatch match in after)
                    if (ReferenceEquals(match.Query, query)) { contains = MatchesMembership(match, record.Chunk!, record.Row); break; }
                if (before != contains) PublishMembership(query, state.Entity, contains);
            }
            foreach (MembershipMatch match in after)
            {
                bool known = false;
                foreach (MembershipMatch before in state.Matches)
                    if (ReferenceEquals(before.Query, match.Query)) { known = true; break; }
                if (!known && MatchesMembership(match, record.Chunk!, record.Row)) PublishMembership(match.Query, state.Entity, true);
            }
        }
        finally { if (state.Words is { } words) ArrayPool<ulong>.Shared.Return(words); }
    }

    private void PublishMembership(QueryData query, Entity entity, bool added)
    {
        query.MembershipVersion = NextVersion();
        EventLog<MembershipChange>? log = query.MembershipLog;
        if (log?.HasReaders != true) return;
        using EventWriter<MembershipChange> writer = log.Reserve(1, Execution?.CurrentAccess ?? default);
        writer.Values[0] = new(entity, added ? MembershipChangeKind.Added : MembershipChangeKind.Removed,
            writer.Range.First + 1);
        log.Publish(writer.Range);
    }

    private void PublishCreatedMembership(ReadOnlySpan<Entity> entities)
    {
        if (_membershipQueries.Count == 0) return;
        PublishMembershipBatch(entities, true);
    }

    private void PublishMembershipBatch(ReadOnlySpan<Entity> entities, bool added)
    {
        int start = 0;
        while (start < entities.Length)
        {
            Chunk chunk = Records[entities[start].Id].Chunk!;
            int end = start + 1;
            while (end < entities.Length && ReferenceEquals(Records[entities[end].Id].Chunk, chunk)) end++;
            ReadOnlySpan<Entity> members = entities[start..end];
            foreach (MembershipMatch match in MembershipMatches(chunk.Archetype))
            {
                int count = 0;
                foreach (Entity entity in members)
                    if (MatchesMembership(match, chunk, Records[entity.Id].Row)) count++;
                if (count == 0) continue;
                match.Query.MembershipVersion = NextVersion();
                if (match.Query.MembershipLog is not { HasReaders: true } log) continue;
                using EventWriter<MembershipChange> writer = log.Reserve(count, Execution?.CurrentAccess ?? default);
                int index = 0;
                foreach (Entity entity in members)
                    if (MatchesMembership(match, chunk, Records[entity.Id].Row))
                    {
                        writer.Values[index] = new(entity,
                            added ? MembershipChangeKind.Added : MembershipChangeKind.Removed,
                            writer.Range.First + (ulong)index + 1);
                        index++;
                    }
                log.Publish(writer.Range);
            }
            start = end;
        }
    }
}
