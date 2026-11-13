using SomeEngine.ECS;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Each persistent set's native query and its sole membership reader.</summary>
internal sealed class PhysicsGraphQueries : IDisposable
{
    private readonly Query?[] _queries;
    private readonly EventReader<MembershipChange>?[] _readers;
    internal World World { get; }
    internal Query Bodies { get; }
    internal JobResourceAccess[] MetadataAccesses
    {
        get
        {
            var accesses = new List<JobResourceAccess>();
            foreach (var reader in _readers) if (reader is not null) accesses.Add(reader.Read);
            return accesses.ToArray();
        }
    }
    internal PhysicsGraphQueries(World world, Query bodies, Query?[] constraints)
    {
        World = world; Bodies = bodies; _queries = constraints;
        _readers = new EventReader<MembershipChange>?[constraints.Length];
        for (int i = 0; i < constraints.Length; i++) if (constraints[i] is { } query) _readers[i] = world.WatchMembership(query);
    }
    internal void ReadMembers(in PhysicsStepContext context, PhysicsGraphData graph)
    {
        for (int set = 0; set < _readers.Length; set++)
        {
            if (_queries[set] is { HasVersionFilters: true }) graph.Clear(set);
            if (_readers[set] is not { } reader) continue;
            using EventRead<MembershipChange> changes = reader.Open();
            foreach (var change in changes.First) if (change.Kind == MembershipChangeKind.Removed) graph.Remove(set, change.Entity);
            foreach (var change in changes.Second) if (change.Kind == MembershipChangeKind.Removed) graph.Remove(set, change.Entity);
            reader.Advance(changes.Through);
        }
    }
    public void Dispose() { foreach (var reader in _readers) reader?.Dispose(); }
}
