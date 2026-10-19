using System.Runtime.CompilerServices;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Relations.Benchmarks;

public sealed record IndexMemory(Dictionary<string, long> PayloadBytes, Dictionary<string, int> Counts)
{
    public static IndexMemory Read(int outputCount, CollisionTree? tree = null,
        int categoryCapacity = 0, int keyCapacity = 0, CollisionPairBuffer? pairs = null,
        int maintenanceAccessCount = 0, int contactCapacity = 0)
    {
        int live = tree is null || tree.LeafCount == 0 ? 0 : 2 * tree.LeafCount - 1;
        int addresses = tree?.NodeCount ?? 0;
        int nodeSize = Unsafe.SizeOf<CollisionNode>();
        int categorySize = Unsafe.SizeOf<CollisionBitmapData>();
        int keySize = Unsafe.SizeOf<CollisionKeyData<Entity>>();
        int pairSize = Unsafe.SizeOf<(Entity, Entity)>();
        int accessSize = Unsafe.SizeOf<JobResourceAccess>();
        int p = pairs?.PartitionCount ?? 0;
        int f = pairs?.FilledPartitionCount ?? 0;
        int capacity = pairs?.PartitionCapacity ?? 0;
        var bytes = new Dictionary<string, long>
        {
            ["TreeLive"] = (long)live * nodeSize,
            ["TreeAddressed"] = (long)addresses * nodeSize,
            ["TreeCapacity"] = (long)(tree?.NodeCapacity ?? 0) * nodeSize,
            ["CategoryLive"] = categoryCapacity == 0 ? 0 : (long)live * categorySize,
            ["CategoryAddressed"] = categoryCapacity == 0 ? 0 : (long)addresses * categorySize,
            ["CategoryCapacity"] = (long)categoryCapacity * categorySize,
            ["KeyLive"] = keyCapacity == 0 ? 0 : (long)live * keySize,
            ["KeyAddressed"] = keyCapacity == 0 ? 0 : (long)addresses * keySize,
            ["KeyCapacity"] = (long)keyCapacity * keySize,
            ["LeafMapKeyValues"] = (long)(tree?.LeafMapCount ?? 0) * (Unsafe.SizeOf<Entity>() + sizeof(int)),
            ["OutputUsed"] = pairs is null ? 0 : (long)outputCount * pairSize,
            ["OutputCapacity"] = (long)(pairs?.PairCapacity ?? 0) * pairSize,
            ["PartitionUsed"] = pairs is null ? 0 : (long)p * Unsafe.SizeOf<CollisionNodePair>() + (long)(2 * p + 1 + f) * sizeof(int),
            ["PartitionCapacity"] = pairs is null ? 0 : (long)capacity * Unsafe.SizeOf<CollisionNodePair>() + (long)(3 * capacity + 1) * sizeof(int),
            ["AccessDataUsed"] = (long)(p + 2 * f) * accessSize,
            ["AccessDataCapacity"] = (long)(3 * capacity) * accessSize,
            ["MaintenanceAccess"] = (long)maintenanceAccessCount * accessSize,
            ["ContactTotals"] = (long)contactCapacity * (sizeof(int) + sizeof(float))
        };
        var counts = new Dictionary<string, int>
        {
            ["TreeLeaves"] = tree?.LeafCount ?? 0,
            ["TreeLiveNodes"] = live,
            ["TreeAddressCount"] = addresses,
            ["TreeNodeCapacity"] = tree?.NodeCapacity ?? 0,
            ["LeafMapEntries"] = tree?.LeafMapCount ?? 0,
            ["LeafMapCapacityEntries"] = tree?.LeafMapCapacity ?? 0,
            ["PartitionCount"] = p,
            ["FilledPartitionCount"] = f,
            ["PartitionCapacity"] = capacity,
            ["OutputCount"] = outputCount,
            ["OutputCapacity"] = pairs?.PairCapacity ?? 0
        };
        return new(bytes, counts);
    }

    public static IndexMemory ReadWorkspace(MeasurementData data, int outputCount)
    {
        int entitySize = Unsafe.SizeOf<Entity>();
        int pairSize = Unsafe.SizeOf<(Entity, Entity)>();
        long bucketPayload = 0;
        if (data.Input.Class == "character")
            foreach (Entity character in data.Characters)
                bucketPayload += (long)data.World.GetByIndex<Hair, Entity>(character).Length * entitySize;
        return new(new()
        {
            ["ParticipantEntities"] = (long)data.Entities.Length * entitySize,
            ["CharacterEntities"] = (long)data.Characters.Length * entitySize,
            ["InitialBodies"] = (long)data.Entities.Length * Unsafe.SizeOf<Body>(),
            ["CharacterNumbers"] = (long)data.Entities.Length * sizeof(int),
            ["BodyPresence"] = (long)data.Entities.Length * sizeof(bool),
            ["Candidates"] = (long)data.Candidates.Length * pairSize,
            ["KeyBuckets"] = bucketPayload,
            ["OutputUsed"] = data.Output.Length == 0 ? 0 : (long)outputCount * pairSize,
            ["OutputCapacity"] = (long)data.Output.Length * pairSize,
            ["ContactTotals"] = (long)data.ContactCounts.Length * sizeof(int) + (long)data.Depths.Length * sizeof(float)
        }, new()
        {
            ["ParticipantCapacity"] = data.Entities.Length,
            ["CharacterCapacity"] = data.Characters.Length,
            ["CandidateCapacity"] = data.Candidates.Length,
            ["OutputCapacity"] = data.Output.Length,
            ["ContactCapacity"] = data.ContactCounts.Length
        });
    }
}
