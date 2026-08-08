using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

public static partial class WorldSerializer
{
    private static int ReadCanonicalManifestIndex(
        BinaryReader reader,
        int itemCount,
        int manifestCount,
        int ordinal,
        ref int previousManifestIndex)
    {
        if (ordinal == 0 && itemCount > manifestCount)
        {
            throw new InvalidDataException(
                "Entity item count exceeds the serialization manifest count.");
        }

        int manifestIndex = reader.ReadInt32();
        if ((uint)manifestIndex >= (uint)manifestCount)
            throw new InvalidDataException($"Invalid manifest index {manifestIndex}.");
        if (manifestIndex <= previousManifestIndex)
        {
            throw new InvalidDataException(
                $"Entity manifest index {manifestIndex} is duplicate or not canonical.");
        }

        previousManifestIndex = manifestIndex;
        return manifestIndex;
    }

    private static SerializationTypeRuntime[] BuildEntityManifest(
        AdmittedWorldWrite admitted,
        Entity entity,
        SerializationRegistry registry)
    {
        var present = new HashSet<SerializationTypeRuntime>();
        AddPresentRuntimes(admitted, entity, registry.RuntimeTypes, present);
        return SortManifest(present);
    }

    private static SerializationTypeRuntime[] BuildEntityManifest(
        AdmittedWorldWrite admitted,
        ReadOnlySpan<Entity> entities,
        SerializationRegistry registry)
    {
        var present = new HashSet<SerializationTypeRuntime>();
        for (int i = 0; i < entities.Length; i++)
            AddPresentRuntimes(admitted, entities[i], registry.RuntimeTypes, present);
        return SortManifest(present);
    }

    private static void AddPresentRuntimes(
        AdmittedWorldWrite admitted,
        Entity entity,
        ReadOnlySpan<SerializationTypeRuntime> runtimes,
        HashSet<SerializationTypeRuntime> present)
    {
        for (int i = 0; i < runtimes.Length; i++)
        {
            SerializationTypeRuntime runtime = runtimes[i];
            if (runtime.IsPresent(admitted, entity))
                present.Add(runtime);
        }
    }

    private static SerializationTypeRuntime[] SortManifest(
        HashSet<SerializationTypeRuntime> present)
    {
        SerializationTypeRuntime[] manifest = present.ToArray();
        Array.Sort(manifest, static (left, right) =>
            SerializationRegistry.CompareTypeKeys(left.Entry.TypeKey, right.Entry.TypeKey));
        return manifest;
    }

    private static Dictionary<Guid, int> BuildManifestIndex(
        ReadOnlySpan<SerializationTypeRuntime> manifest)
    {
        var index = new Dictionary<Guid, int>(manifest.Length);
        for (int i = 0; i < manifest.Length; i++)
            index.Add(manifest[i].Entry.TypeKey.StableId, i);
        return index;
    }

    private static SerializationTypeRuntime[] ResolveManifest(
        SerializationRegistry registry,
        ReadOnlySpan<SerializationTypeKey> keys,
        SerializationContract contract)
    {
        var manifest = new SerializationTypeRuntime[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            SerializationTypeRuntime runtime = registry.ResolveExact(keys[i]);
            PayloadFormat.ValidateReadContract(contract, runtime);
            manifest[i] = runtime;
        }

        return manifest;
    }
}
