using System.Buffers;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal static class ExternalReferenceChecks
{
    internal static void Run()
    {
        ExternalFieldsCollectTheirNonemptyKeys();
        ComponentCodecResolvesTheTargetAsset();
        BufferCodecResolvesItsElements();
        WorldStreamSaveCollectsExternalKeys();
        WorldBufferSaveCollectsExternalKeys();
        CheckpointStreamSaveCollectsExternalKeys();
        CheckpointBufferSaveCollectsExternalKeys();
        BackgroundSaveCollectsExternalKeys();
        FileSaveCollectsExternalKeys();
        BackgroundFileSaveCollectsExternalKeys();
        WorldSaveBoundsSparseMemberships();
        CheckpointSaveBoundsSparseMemberships();
        BackgroundSaveBoundsTopologyRecords();
        CheckpointSaveBoundsTopologyBytes();
        Console.WriteLine("External references: 16-byte keys, caller-owned key collection and target resolver binding passed.");
    }

    private static void ExternalFieldsCollectTheirNonemptyKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var references = new HashSet<ExternalReferenceKey>();
        var output = new ArrayBufferWriter<byte>();
        var writer = new DataWriter(output, references);
        writer.WriteExternalReference(key);
        writer.WriteExternalReference(default);
        writer.WriteExternalReference(key);
        writer.Flush();
        var reader = new DataReader(output.WrittenSpan);
        Require(output.WrittenCount == 48 && reader.ReadExternalReference() == key
            && reader.ReadExternalReference() == default && reader.ReadExternalReference() == key
            && references.SetEquals([key]),
            "external fields retain their Guid values and collect each nonempty key");
    }

    private static void ComponentCodecResolvesTheTargetAsset()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var original = new Asset(key, "source");
        var resolved = new Asset(key, "target");
        using var source = new World();
        var entity = source.Create(new AssetReference(original));
        var writeRegistry = new SerializationRegistry()
            .RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(original)));
        using var output = new MemoryStream();
        new WorldSerializer(writeRegistry).Save(source, output, SavePurpose.DurableSave);
        output.Position = 0;
        using var target = new World();
        var readRegistry = new SerializationRegistry()
            .RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(resolved)));
        new WorldSerializer(readRegistry).Load(target, output);
        Require(ReferenceEquals(target.ReadValue<AssetReference>(entity).Value, resolved),
            "a loaded component uses the resolver instance held by its target registry");
    }

    private static void BufferCodecResolvesItsElements()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var original = new Asset(key, "source");
        var resolved = new Asset(key, "target");
        using var source = new World();
        var entity = source.Create();
        source.AddBuffer<AssetReference>(entity);
        using (var values = source.GetBuffer<AssetReference>(entity))
        {
            values.Add(new(original));
            values.Add(default);
        }
        var writeRegistry = new SerializationRegistry()
            .RegisterCodec<AssetReference, AssetCodec>(new(new AssetResolver(original)))
            .RegisterBufferCodec<AssetReference, AssetCodec>(new(new AssetResolver(original)));
        var output = new ArrayBufferWriter<byte>();
        new WorldSerializer(writeRegistry).Save(source, output, SavePurpose.DurableSave);
        using var target = new World();
        var readRegistry = new SerializationRegistry()
            .RegisterCodec<AssetReference, AssetCodec>(new(new AssetResolver(resolved)))
            .RegisterBufferCodec<AssetReference, AssetCodec>(new(new AssetResolver(resolved)));
        new WorldSerializer(readRegistry).Load(target, output.WrittenSpan);
        using var elements = target.ReadBuffer<AssetReference>(entity);
        Require(elements.Count == 2 && ReferenceEquals(elements[0].Value, resolved) && elements[1].Value is null,
            "buffer elements use their registered resolver and retain empty references");
    }

    private static void WorldStreamSaveCollectsExternalKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var asset = new Asset(key, "world stream");
        using var world = new World();
        world.Create(new AssetReference(asset));
        var registry = new SerializationRegistry().RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(asset)));
        var references = new HashSet<ExternalReferenceKey>();
        using var output = new MemoryStream();
        new WorldSerializer(registry).Save(world, output,
            new SerializeOptions(Purpose: SavePurpose.DurableSave, ExternalReferences: references));
        output.Position = 0;
        using World target = WorldSerializer.ReadWorld(output, registry, new(RequiredPurpose: SavePurpose.DurableSave));
        Require(references.SetEquals([key]) && target.EntityCount == 1,
            "World stream saves use the selected format and collect keys into the caller's set");
    }

    private static void WorldBufferSaveCollectsExternalKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var asset = new Asset(key, "world buffer");
        using var world = new World();
        world.Create(new AssetReference(asset));
        var registry = new SerializationRegistry().RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(asset)));
        var references = new HashSet<ExternalReferenceKey>();
        var output = new ArrayBufferWriter<byte>();
        new WorldSerializer(registry).Save(world, output,
            new SerializeOptions(ExternalReferences: references));
        Require(output.WrittenCount > 0 && references.SetEquals([key]),
            "World buffer saves collect keys into the caller's set");
    }

    private static void CheckpointStreamSaveCollectsExternalKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var asset = new Asset(key, "checkpoint stream");
        using var world = new World();
        world.Create(new AssetReference(asset));
        var registry = new SerializationRegistry().RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(asset)));
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        var references = new HashSet<ExternalReferenceKey>();
        using var output = new MemoryStream();
        new WorldSerializer(registry).Save(checkpoint, output, new SerializeOptions(ExternalReferences: references));
        Require(output.Length > 0 && references.SetEquals([key]),
            "checkpoint stream saves collect keys while encoding their captured values");
    }

    private static void CheckpointBufferSaveCollectsExternalKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var asset = new Asset(key, "checkpoint buffer");
        using var world = new World();
        world.Create(new AssetReference(asset));
        var registry = new SerializationRegistry().RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(asset)));
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        var references = new HashSet<ExternalReferenceKey>();
        var output = new ArrayBufferWriter<byte>();
        new WorldSerializer(registry).Save(checkpoint, output, new SerializeOptions(ExternalReferences: references));
        Require(output.WrittenCount > 0 && references.SetEquals([key]),
            "checkpoint buffer saves collect keys while encoding their captured values");
    }

    private static void BackgroundSaveCollectsExternalKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var asset = new Asset(key, "background");
        using var world = new World();
        world.Create(new AssetReference(asset));
        var registry = new SerializationRegistry().RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(asset)));
        var references = new HashSet<ExternalReferenceKey>();
        using var output = new MemoryStream();
        new WorldSerializer(registry).SaveAsync(world, output,
            new SerializeOptions(Purpose: SavePurpose.DurableSave, ExternalReferences: references)).GetAwaiter().GetResult();
        Require(output.Length > 0 && references.SetEquals([key]),
            "background saves retain the caller's key set through encoding completion");
    }

    private static void FileSaveCollectsExternalKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var asset = new Asset(key, "file");
        using var world = new World();
        world.Create(new AssetReference(asset));
        var registry = new SerializationRegistry().RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(asset)));
        var references = new HashSet<ExternalReferenceKey>();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("ecs-external-");
        try
        {
            string path = Path.Combine(directory.FullName, "world.bin");
            new WorldSerializer(registry).SaveFile(world, path,
                new SerializeOptions(Purpose: SavePurpose.DurableSave, ExternalReferences: references));
            using Stream input = AtomicSaveFile.OpenRead(path);
            Require(input.Length > 0 && references.SetEquals([key]),
                "file saves commit their encoded values and collect keys into the caller's set");
        }
        finally { directory.Delete(true); }
    }

    private static void BackgroundFileSaveCollectsExternalKeys()
    {
        var key = new ExternalReferenceKey(Guid.NewGuid());
        var asset = new Asset(key, "background file");
        using var world = new World();
        world.Create(new AssetReference(asset));
        var registry = new SerializationRegistry().RegisterCodec<AssetReference, AssetCodec>(Key(1), new(new AssetResolver(asset)));
        var references = new HashSet<ExternalReferenceKey>();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("ecs-external-");
        try
        {
            string path = Path.Combine(directory.FullName, "world.bin");
            new WorldSerializer(registry).SaveFileAsync(world, path,
                new SerializeOptions(Purpose: SavePurpose.DurableSave, ExternalReferences: references)).GetAwaiter().GetResult();
            using Stream input = AtomicSaveFile.OpenRead(path);
            Require(input.Length > 0 && references.SetEquals([key]),
                "background file saves retain the caller's key set through atomic commit completion");
        }
        finally { directory.Delete(true); }
    }

    private static void WorldSaveBoundsSparseMemberships()
    {
        using var world = new World();
        world.Set(world.Create(), 10);
        world.Set(world.Create(), 20);
        var registry = new SerializationRegistry().RegisterSparse<int>(Key(1));
        var serializer = new WorldSerializer(registry);
        var output = new ArrayBufferWriter<byte>();
        serializer.Save(world, output, new SerializeOptions(MaximumSparseMemberships: 2));
        bool exceeded = false;
        try { serializer.Save(world, new ArrayBufferWriter<byte>(), new SerializeOptions(MaximumSparseMemberships: 1)); }
        catch (InvalidOperationException) { exceeded = true; }
        Require(output.WrittenCount > 0 && exceeded, "World saves apply the limit to total sparse memberships");
    }

    private static void CheckpointSaveBoundsSparseMemberships()
    {
        using var world = new World();
        world.Set(world.Create(), 10);
        world.Set(world.Create(), 20);
        var registry = new SerializationRegistry().RegisterSparse<int>(Key(1));
        var serializer = new WorldSerializer(registry);
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        var output = new ArrayBufferWriter<byte>();
        serializer.Save(checkpoint, output, new SerializeOptions(MaximumSparseMemberships: 2));
        bool exceeded = false;
        try { serializer.Save(checkpoint, new ArrayBufferWriter<byte>(), new SerializeOptions(MaximumSparseMemberships: 1)); }
        catch (InvalidOperationException) { exceeded = true; }
        Require(output.WrittenCount > 0 && exceeded, "checkpoint saves apply the limit to captured sparse memberships");
    }

    private static void BackgroundSaveBoundsTopologyRecords()
    {
        using var world = new World();
        var parent = world.Create();
        var child = world.Create();
        Hierarchy<SaveTree>.SetParent(world, child, parent);
        var copies = new ResourceCopies();
        world.SetResource(new CountedResource(copies));
        var registry = new SerializationRegistry().RegisterHierarchy<SaveTree>(Key(1))
            .RegisterResource<CountedResource, CountedResourceCodec>(Key(2));
        var serializer = new WorldSerializer(registry);
        using var output = new MemoryStream();
        serializer.SaveAsync(world, output, new SerializeOptions(MaximumTopologyRecords: 1024)).GetAwaiter().GetResult();
        int copied = copies.Count;
        bool exceeded = false;
        using var limited = new MemoryStream();
        try { serializer.SaveAsync(world, limited, new SerializeOptions(MaximumTopologyRecords: 1)).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { exceeded = true; }
        Require(output.Length > 0 && exceeded && copied == 1 && copies.Count == copied,
            "background saves enforce the topology record limit before cloning resource values");
    }

    private static void CheckpointSaveBoundsTopologyBytes()
    {
        using var world = new World();
        var parent = world.Create();
        var child = world.Create();
        Hierarchy<SaveTree>.SetParent(world, child, parent);
        var registry = new SerializationRegistry().RegisterHierarchy<SaveTree>(Key(1));
        var serializer = new WorldSerializer(registry);
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        var output = new ArrayBufferWriter<byte>();
        serializer.Save(checkpoint, output, new SerializeOptions(MaximumTopologyPayloadBytes: 1024));
        bool exceeded = false;
        try { serializer.Save(checkpoint, new ArrayBufferWriter<byte>(), new SerializeOptions(MaximumTopologyPayloadBytes: 1)); }
        catch (InvalidOperationException) { exceeded = true; }
        Require(output.WrittenCount > 0 && exceeded, "checkpoint saves bound the topology bytes encoded to their destination");
    }

    private struct SaveTree : IHierarchyDomain;
    private sealed class ResourceCopies { internal int Count; }
    private sealed class CountedResource(ResourceCopies copies) : ICloneable
    {
        public object Clone() { copies.Count++; return new CountedResource(copies); }
    }
    private readonly struct CountedResourceCodec : IValueCodec<CountedResource>
    {
        public void Write(ref DataWriter writer, in CountedResource value) => writer.WriteByte(0);
        public void Read(ref DataReader reader, out CountedResource value) { reader.ReadByte(); value = new(new()); }
    }

    private sealed record Asset(ExternalReferenceKey Key, string Name);
    private readonly record struct AssetReference(Asset? Value);

    private sealed class AssetResolver(Asset asset) : IExternalResolver
    {
        public bool TryResolve(ExternalReferenceKey id, out object value)
        {
            value = asset;
            return id == asset.Key;
        }
    }

    private readonly struct AssetCodec(IExternalResolver resolver) : IComponentCodec<AssetReference>
    {
        public void Write(ref DataWriter writer, in AssetReference value)
            => writer.WriteExternalReference(value.Value?.Key ?? default);

        public void Read(ref DataReader reader, out AssetReference value)
        {
            ExternalReferenceKey key = reader.ReadExternalReference();
            if (key.Value == Guid.Empty) value = default;
            else if (resolver.TryResolve(key, out object resolved) && resolved is Asset asset) value = new(asset);
            else throw new InvalidDataException("The asset key could not be resolved to its declared type.");
        }
    }

    private static SerializationTypeKey Key(int value)
        => new(new Guid(value, 0x31fa, 0x47c2, 0x8b, 0x17, 0x51, 0x32, 0x12, 0x77, 0x31, 0x40), "External.Asset." + value, 1);

    private static void Require(bool condition, string behavior)
    { if (!condition) throw new InvalidOperationException(behavior); }
}
