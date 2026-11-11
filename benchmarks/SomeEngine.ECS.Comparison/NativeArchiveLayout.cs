namespace SomeEngine.ECS.Comparison;

internal sealed class NativeArchiveGroup(int id, ArchiveIdentity[] identities)
{
    internal readonly int Id = id;
    internal readonly ArchiveIdentity[] Identities = identities;
    internal ulong Types;
}
internal sealed class NativeArchiveColumnGroups(NativeArchiveGroup[] groups)
{ internal readonly NativeArchiveGroup[] Groups = groups; }
internal sealed class NativeArchiveLayout
{
    internal readonly List<NativeArchiveGroup> Groups = new();
    internal NativeArchiveColumnGroups[] Columns = [];
    internal bool DisjointGroups;
    private readonly Dictionary<(ArchiveIdentity First, int Count), List<NativeArchiveGroup>> _candidates = new();
    internal NativeArchiveGroup Bind(ReadOnlySpan<ArchiveIdentity> identities)
    {
        var key = (identities[0], identities.Length);
        if (!_candidates.TryGetValue(key, out var candidates)) _candidates.Add(key, candidates = new());
        foreach (var candidate in candidates) if (identities.SequenceEqual(candidate.Identities)) return candidate;
        var group = new NativeArchiveGroup(Groups.Count, identities.ToArray()); Groups.Add(group); candidates.Add(group); return group;
    }
}
