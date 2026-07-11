namespace SomeEngine.RenderGraph;

internal sealed class TransientPlacement
{
    public TransientPlacement(
        CompiledHeap[] heaps,
        CompiledPlacement[] placements,
        AliasAcquireEdge[] acquires,
        CompiledAliasingStatistics statistics)
    {
        Heaps = heaps;
        Placements = placements;
        Acquires = acquires;
        Statistics = statistics;
    }

    public CompiledHeap[] Heaps { get; }
    public CompiledPlacement[] Placements { get; }
    public AliasAcquireEdge[] Acquires { get; }
    public CompiledAliasingStatistics Statistics { get; }
}

internal readonly record struct AliasAcquireEdge(
    int BeforeResource,
    int AfterResource,
    int[] EndPasses,
    int[] StartPasses);

internal static class TransientAliasAllocator
{
    public static TransientPlacement Place(
        FrozenGraph graph,
        bool[] liveResources,
        PassReachability? order,
        bool enableAliasing)
    {
        int[] resources = Enumerable.Range(0, graph.Resources.Length)
            .Where(resource => liveResources[resource] && !graph.Resources[resource].IsImported)
            .ToArray();
        if (!enableAliasing) return PlaceWithoutAliasing(graph, resources);

        PassReachability reachability = order ??
            throw new ArgumentNullException(nameof(order), "Aliasing requires pass reachability.");
        int[][] resourceUses = BuildResourceUses(graph, liveResources, reachability.ActivePassOrdinals);

        ulong logicalRequestedBytes = SumRequirements(graph, resources);
        ulong nonAliasedPlacedBytes = ComputeNonAliasedHeapBytes(graph, resources);
        List<AliasSlot> slots = [];
        foreach (int resource in resources
                     .OrderByDescending(resource => graph.Resources[resource].Requirements.Size)
                     .ThenByDescending(resource => graph.Resources[resource].Requirements.Alignment)
                     .ThenBy(static resource => resource))
        {
            ResourceRequirements requirements = graph.Resources[resource].Requirements;
            bool eligible = enableAliasing &&
                            requirements.MemoryType == MemoryType.DeviceLocal &&
                            FirstUsesInitialize(graph, resource, resourceUses[resource], reachability);
            AliasSlot? selected = null;
            ulong selectedGrowth = ulong.MaxValue;
            if (eligible)
            {
                foreach (AliasSlot slot in slots)
                {
                    if (!slot.Aliasable || !slot.ProfileMatches(requirements) ||
                        !slot.Resources.All(occupant => reachability.CompareUses(resourceUses[occupant], resourceUses[resource]) != 0))
                    {
                        continue;
                    }

                    ulong alignment = Math.Max(slot.Alignment, requirements.Alignment);
                    ulong capacity = AlignUp(Math.Max(slot.MaximumSize, requirements.Size), alignment);
                    ulong growth = checked(capacity - slot.Capacity);
                    if (selected is null || growth < selectedGrowth || growth == selectedGrowth && slot.Id < selected.Id)
                    {
                        selected = slot;
                        selectedGrowth = growth;
                    }
                }
            }

            if (selected is null)
            {
                selected = new AliasSlot(slots.Count, requirements, eligible);
                slots.Add(selected);
            }
            selected.Add(resource, requirements);
        }

        foreach (AliasSlot slot in slots)
        {
            slot.Resources.Sort((left, right) =>
            {
                int comparison = reachability.CompareUses(resourceUses[left], resourceUses[right]);
                if (comparison == 0 && left != right)
                    throw new InvalidOperationException("One alias slot contains resources with incomparable lifetimes.");
                return comparison;
            });
        }

        PlaceSlots(graph.Resources.Length, slots, out CompiledHeap[] heaps, out CompiledPlacement[] placements);
        List<AliasAcquireEdge> acquires = [];
        foreach (AliasSlot slot in slots)
        {
            for (int index = 1; index < slot.Resources.Count; index++)
            {
                int before = slot.Resources[index - 1];
                int after = slot.Resources[index];
                acquires.Add(new AliasAcquireEdge(
                    before,
                    after,
                    reachability.EndFrontier(resourceUses[before]),
                    reachability.StartFrontier(resourceUses[after])));
            }
        }

        ulong plannedHeapBytes = SumHeaps(heaps);
        ulong savings = nonAliasedPlacedBytes >= plannedHeapBytes
            ? nonAliasedPlacedBytes - plannedHeapBytes
            : 0;
        CompiledAliasingStatistics statistics = new(
            enableAliasing,
            logicalRequestedBytes,
            nonAliasedPlacedBytes,
            plannedHeapBytes,
            savings,
            slots.Count,
            acquires.Count);
        return new TransientPlacement(heaps, placements, acquires.ToArray(), statistics);
    }

    private static TransientPlacement PlaceWithoutAliasing(FrozenGraph graph, int[] resources)
    {
        List<AliasSlot> slots = [];
        foreach (int resource in resources
                     .OrderByDescending(resource => graph.Resources[resource].Requirements.Size)
                     .ThenByDescending(resource => graph.Resources[resource].Requirements.Alignment)
                     .ThenBy(static resource => resource))
        {
            ResourceRequirements requirements = graph.Resources[resource].Requirements;
            AliasSlot slot = new(slots.Count, requirements, aliasable: false);
            slot.Add(resource, requirements);
            slots.Add(slot);
        }

        PlaceSlots(graph.Resources.Length, slots, out CompiledHeap[] heaps, out CompiledPlacement[] placements);
        ulong logicalRequestedBytes = SumRequirements(graph, resources);
        ulong nonAliasedPlacedBytes = ComputeNonAliasedHeapBytes(graph, resources);
        return new TransientPlacement(
            heaps,
            placements,
            [],
            new CompiledAliasingStatistics(
                Enabled: false,
                logicalRequestedBytes,
                nonAliasedPlacedBytes,
                SumHeaps(heaps),
                AliasSavingsBytes: 0,
                AliasSlotCount: slots.Count,
                AliasAcquireCount: 0));
    }

    private static int[][] BuildResourceUses(FrozenGraph graph, bool[] liveResources, int[] activePassOrdinals)
    {
        List<int>[] uses = Enumerable.Range(0, graph.Resources.Length)
            .Select(static _ => new List<int>())
            .ToArray();
        foreach (int pass in activePassOrdinals)
        {
            foreach (int resource in graph.Passes[pass].Accesses
                         .Select(static access => access.Resource)
                         .Distinct()
                         .Order())
            {
                if (liveResources[resource]) uses[resource].Add(pass);
            }
        }
        return uses.Select(static value => value.ToArray()).ToArray();
    }

    private static bool FirstUsesInitialize(FrozenGraph graph, int resource, int[] uses, PassReachability order)
    {
        foreach (int pass in order.StartFrontier(uses))
        foreach (FrozenAccess access in graph.Passes[pass].Accesses)
        {
            if (access.Resource != resource) continue;
            if (access.Effect != ResourceEffect.Write ||
                access.PriorContents != PriorContents.Discard ||
                access.Coverage != WriteCoverage.Full)
            {
                return false;
            }
        }
        return uses.Length != 0;
    }

    private static void PlaceSlots(
        int resourceCount,
        List<AliasSlot> slots,
        out CompiledHeap[] heaps,
        out CompiledPlacement[] placements)
    {
        placements = Enumerable.Repeat(new CompiledPlacement(-1, 0), resourceCount).ToArray();
        List<CompiledHeap> heapValues = [];
        IEnumerable<IGrouping<ProfileKey, AliasSlot>> groups = slots
            .GroupBy(static slot => slot.Profile)
            .OrderBy(static group => group.Key.MemoryType)
            .ThenBy(static group => group.Key.ResourceClass)
            .ThenBy(static group => group.Key.CompatibilityClass);
        foreach (IGrouping<ProfileKey, AliasSlot> group in groups)
        {
            int heap = heapValues.Count;
            ulong size = 0;
            foreach (AliasSlot slot in group
                         .OrderByDescending(static slot => slot.Capacity)
                         .ThenByDescending(static slot => slot.Alignment)
                         .ThenBy(static slot => slot.Id))
            {
                ulong offset = AlignUp(size, slot.Alignment);
                size = checked(offset + slot.Capacity);
                foreach (int resource in slot.Resources) placements[resource] = new CompiledPlacement(heap, offset);
            }
            heapValues.Add(new CompiledHeap(
                size,
                group.Key.MemoryType,
                group.Key.ResourceClass,
                group.Key.CompatibilityClass));
        }
        heaps = heapValues.ToArray();
    }

    private static ulong ComputeNonAliasedHeapBytes(FrozenGraph graph, int[] resources)
    {
        ulong result = 0;
        foreach (IGrouping<ProfileKey, int> group in resources
                     .GroupBy(resource => ProfileKey.From(graph.Resources[resource].Requirements)))
        {
            ulong size = 0;
            foreach (int resource in group
                         .OrderByDescending(resource => AlignUp(
                             graph.Resources[resource].Requirements.Size,
                             graph.Resources[resource].Requirements.Alignment))
                         .ThenByDescending(resource => graph.Resources[resource].Requirements.Alignment)
                         .ThenBy(static resource => resource))
            {
                ResourceRequirements requirements = graph.Resources[resource].Requirements;
                size = checked(
                    AlignUp(size, requirements.Alignment) +
                    AlignUp(requirements.Size, requirements.Alignment));
            }
            result = checked(result + size);
        }
        return result;
    }

    private static ulong SumRequirements(FrozenGraph graph, int[] resources)
    {
        ulong result = 0;
        foreach (int resource in resources)
            result = checked(result + graph.Resources[resource].Requirements.Size);
        return result;
    }

    private static ulong SumHeaps(CompiledHeap[] heaps)
    {
        ulong result = 0;
        foreach (CompiledHeap heap in heaps) result = checked(result + heap.Size);
        return result;
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        alignment <= 1 ? value : checked(((value + alignment - 1) / alignment) * alignment);

    private readonly record struct ProfileKey(
        MemoryType MemoryType,
        ResourceHeapClass ResourceClass,
        ulong CompatibilityClass)
    {
        public static ProfileKey From(in ResourceRequirements requirements) =>
            new(requirements.MemoryType, requirements.ResourceClass, requirements.CompatibilityClass);
    }

    private sealed class AliasSlot
    {
        public AliasSlot(int id, in ResourceRequirements profile, bool aliasable)
        {
            Id = id;
            Aliasable = aliasable;
            Profile = ProfileKey.From(profile);
            Alignment = profile.Alignment;
            MaximumSize = profile.Size;
            Capacity = AlignUp(profile.Size, profile.Alignment);
        }

        public int Id { get; }
        public bool Aliasable { get; }
        public ProfileKey Profile { get; }
        public List<int> Resources { get; } = [];
        public ulong Alignment { get; private set; }
        public ulong MaximumSize { get; private set; }
        public ulong Capacity { get; private set; }

        public bool ProfileMatches(in ResourceRequirements requirements) => Profile == ProfileKey.From(requirements);

        public void Add(int resource, in ResourceRequirements requirements)
        {
            Resources.Add(resource);
            Alignment = Math.Max(Alignment, requirements.Alignment);
            MaximumSize = Math.Max(MaximumSize, requirements.Size);
            Capacity = AlignUp(MaximumSize, Alignment);
        }
    }

}
