using SomeEngine.Assets;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public sealed class AssetTypeExtensibilityTests
{
    [Fact]
    public void GeneratedDescriptorIsClosedOverTheSingleAssetType()
    {
        AssetAttribute attribute = Assert.Single(
            typeof(ProbeAsset).GetCustomAttributes<AssetAttribute>());
        AssetTypeDescriptor<ProbeAsset> descriptor = AssetType<ProbeAsset>.Descriptor;

        Assert.Equal(".probe.asset", attribute.PathSuffix);
        Assert.Equal(typeof(ProbeAsset).FullName, descriptor.AssetType);
        Assert.Equal(ProbeAsset.TypeId, descriptor.WireType.TypeId);
        Assert.Equal(ProbeAsset.SchemaFingerprint, descriptor.SchemaFingerprint);
        Assert.Equal(typeof(ProbeAsset), descriptor.GetAssetGuid.Method.GetParameters()[0].ParameterType);
        Assert.False(typeof(AssetAttribute).IsGenericTypeDefinition);
        ConstructorInfo assetConstructor = Assert.Single(typeof(AssetAttribute).GetConstructors());
        ParameterInfo suffixParameter = Assert.Single(assetConstructor.GetParameters());
        Assert.Equal(typeof(string), suffixParameter.ParameterType);
        Assert.False(suffixParameter.IsOptional);
        Assert.DoesNotContain(
            typeof(ProbeAsset).GetInterfaces(),
            static type => type.Name.StartsWith("IAsset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewAssetTypeNeedsNoCentralRegistrationAndEncodesOnce()
    {
        string directory = CreateTempDir();
        try
        {
            var project = new AssetProject(directory, []);
            ProbeAsset.ResetEncodingCount();
            var asset = new ProbeAsset
            {
                Name = "No central registration",
                Value = 42,
            };
            AssetGuid guid = project.CreateAsset("assets/probes/value.probe.asset", asset);

            Assert.False(guid.IsEmpty);
            Assert.Equal(guid.ToFlatString(), asset.AssetGuid);
            Assert.Equal(1, ProbeAsset.EncodingCount);
            Assert.True(project.Manifest.TryGetAsset(guid, out AssetManifestRecord record));
            Assert.Equal(typeof(ProbeAsset).FullName, record.AssetType);
            Assert.Equal(ProbeAsset.SchemaFingerprint, record.SchemaFingerprint);

            var state = new ProbeLoadState();
            AssetLoaderOptions options = AssetLoaderOptions.Empty.With(state);
            await using var loader = new AssetLoader(project.CreateStorage(), options);
            AssetHandle<ProbeAsset> first = loader.Load(new AssetId<ProbeAsset>(guid));
            AssetHandle<ProbeAsset> second = loader.Load(new AssetId<ProbeAsset>(guid));
            AssetHandle<ProbeAsset>[] handles = await Task.WhenAll(
                loader.WaitAsync(first).AsTask(),
                loader.WaitAsync(second).AsTask());
            using AssetRead<ProbeAsset> firstRead = loader.Read(handles[0]);
            using AssetRead<ProbeAsset> secondRead = loader.Read(handles[1]);
            ProbeAsset loaded = firstRead.Value;

            Assert.Equal(handles[0], handles[1]);
            Assert.Same(loaded, secondRead.Value);
            Assert.Equal(guid.ToFlatString(), loaded.AssetGuid);
            Assert.Equal(42, loaded.Value);
            Assert.Equal(1, state.LoadCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingFileRequiresExplicitTypedPublicationAndNoFormatProbe()
    {
        string directory = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            const string relativePath = "assets/probes/external.probe.asset";
            string fullPath = Path.Combine(
                directory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var asset = new ProbeAsset
            {
                AssetGuid = guid.ToFlatString(),
                Name = "Explicit publication",
                Value = 7,
            };
            AssetWriter.Write(asset, fullPath);

            AssetProject project = AssetAuthoring.CreateProject(directory);
            await Assert.ThrowsAsync<NotSupportedException>(
                async () => await project.ImportAsync(relativePath));
            Assert.Null(project.Resolve(relativePath));
            Assert.Empty(project.Manifest.Assets);

            AssetGuid registered = await project.RegisterAssetAsync<ProbeAsset>(relativePath);
            Assert.Equal(guid, registered);
            Assert.Equal(guid, project.Resolve(relativePath));

            IAssetStorage storage = project.CreateStorage();
            Assert.True(storage.TryFind(guid, out AssetEntry entry));
            Assert.Equal(typeof(ProbeAsset).FullName, entry.AssetType);
            Assert.Equal(ProbeAsset.SchemaFingerprint, entry.SchemaFingerprint);
            await using BinaryDocument<ProbeAsset> document =
                await AssetProject.OpenAsync<ProbeAsset>(storage, entry);
            Assert.Equal(7, document.Root.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ThirdPartyStorageSuppliesRangesWithoutOwningDecoding()
    {
        string directory = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            string path = Path.Combine(directory, "external.probe.asset");
            AssetWriter.Write(new ProbeAsset
            {
                AssetGuid = guid.ToFlatString(),
                Name = "Third-party storage",
                Value = 73,
            }, path);

            IAssetStorage storage = new ThirdPartyStorage(
                new AssetEntry(
                    guid,
                    typeof(ProbeAsset).FullName!,
                    ProbeAsset.SchemaFingerprint,
                    Guid.NewGuid()),
                path);
            Assert.True(storage.TryFind(guid, out AssetEntry entry));

            await using (BinaryDocument<ProbeAsset> document =
                await AssetProject.OpenAsync<ProbeAsset>(storage, entry))
            {
                Assert.Equal(73, document.Root.Value);
            }

            await using var loader = new AssetLoader(storage);
            AssetHandle<ProbeAsset> handle = loader.Load(new AssetId<ProbeAsset>(guid));
            await loader.WaitAsync(handle);
            using AssetRead<ProbeAsset> read = loader.Read(handle);
            ProbeAsset loaded = read.Value;
            Assert.Equal(73, loaded.Value);
            Assert.Equal(guid.ToFlatString(), loaded.AssetGuid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ThirdPartyStorageCanPublishANewVersionForTheSameStrongHandle()
    {
        string directory = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            string firstPath = Path.Combine(directory, "first.probe.asset");
            string secondPath = Path.Combine(directory, "second.probe.asset");
            AssetWriter.Write(new ProbeAsset
            {
                AssetGuid = guid.ToFlatString(),
                Name = "first publication",
                Value = 11,
            }, firstPath);
            AssetWriter.Write(new ProbeAsset
            {
                AssetGuid = guid.ToFlatString(),
                Name = "second publication",
                Value = 29,
            }, secondPath);

            var storage = new MutableThirdPartyStorage();
            storage.Publish(Entry(guid), firstPath);
            var state = new ProbeLoadState();
            await using var loader = new AssetLoader(
                storage,
                AssetLoaderOptions.Empty.With(state));
            AssetHandle<ProbeAsset> handle = loader.Load(new AssetId<ProbeAsset>(guid));
            await loader.WaitAsync(handle);
            using (AssetRead<ProbeAsset> first = loader.Read(handle))
                Assert.Equal(11, first.Value.Value);

            storage.Publish(Entry(guid), secondPath);
            AssetHandle<ProbeAsset> reloaded = await loader.ReloadAsync(handle);

            Assert.Equal(handle, reloaded);
            Assert.Equal(AssetLoadState.Ready, handle.LoadState);
            Assert.Equal<ulong>(2, handle.Revision);
            Assert.Equal(2, state.LoadCount);
            using AssetRead<ProbeAsset> second = loader.Read(handle);
            Assert.Equal(29, second.Value.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static AssetEntry Entry(AssetGuid guid) => new(
            guid,
            typeof(ProbeAsset).FullName!,
            ProbeAsset.SchemaFingerprint,
            Guid.NewGuid());
    }

    [Fact]
    public async Task ParentStrongHandleRetainsDependenciesUntilParentRetirementFinishes()
    {
        string directory = CreateTempDir();
        try
        {
            var project = new AssetProject(directory, []);
            AssetGuid childGuid = project.CreateAsset(
                "assets/probes/child.childprobe.asset",
                new ChildProbe { Name = "child" });
            AssetGuid parentGuid = project.CreateAsset(
                "assets/probes/parent.parentprobe.asset",
                new ParentProbe
                {
                    Name = "parent",
                    ChildGuid = childGuid.ToFlatString(),
                });
            DependencyRetirementOrder.Reset();
            await using var loader = new AssetLoader(project.CreateStorage());

            (WeakReference parent, WeakReference child) =
                await LoadDependencyGraphAndReleaseAsync(loader, parentGuid, childGuid);

            for (int attempt = 0;
                 attempt < 16 && (parent.IsAlive || child.IsAlive);
                 attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(10);
            }

            Assert.False(parent.IsAlive);
            Assert.False(child.IsAlive);
            Assert.Equal(1, DependencyRetirementOrder.ParentDisposals);
            Assert.Equal(1, DependencyRetirementOrder.ChildDisposals);
            Assert.True(DependencyRetirementOrder.Parent > 0);
            Assert.True(DependencyRetirementOrder.Child > DependencyRetirementOrder.Parent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TypedOpenRejectsNonCurrentFingerprintBeforeOpeningBytes()
    {
        string directory = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            const string relativePath = "assets/probes/wrong-fingerprint.probe.asset";
            string fullPath = Path.Combine(
                directory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var asset = new ProbeAsset
            {
                AssetGuid = guid.ToFlatString(),
                Name = "Wrong fingerprint",
                Value = 11,
            };
            AssetWriter.Write(asset, fullPath);

            ulong wrongFingerprint = ProbeAsset.SchemaFingerprint ^ 1UL;
            var manifest = new AssetManifest();
            manifest.AddAsset(
                guid,
                "Wrong fingerprint",
                relativePath,
                typeof(ProbeAsset).FullName!,
                wrongFingerprint);

            IAssetStorage storage = new LooseAssetStorage(directory, manifest);
            Assert.True(storage.TryFind(guid, out AssetEntry entry));
            await Assert.ThrowsAsync<BinarySchemaMismatchException>(
                async () => await AssetProject.OpenAsync<ProbeAsset>(storage, entry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(TrackingLoadMode.ReturnDifferentRoot, 2)]
    [InlineData(TrackingLoadMode.ReturnWithoutOpen, 1)]
    [InlineData(TrackingLoadMode.ThrowAfterTransfer, 1)]
    public async Task TransferredDocumentFailuresReleaseEveryUnpublishedOwner(
        TrackingLoadMode mode,
        int expectedDisposals)
    {
        string directory = CreateTempDir();
        try
        {
            var project = new AssetProject(directory, []);
            AssetGuid guid = project.CreateAsset(
                "assets/probes/transfer.tracking.asset",
                new TrackingAsset { Name = "transfer", Value = 19 });

            TrackingAsset.Reset();
            AssetLoaderOptions options = AssetLoaderOptions.Empty.With(new TrackingLoadOptions(mode));
            await using (var loader = new AssetLoader(project.CreateStorage(), options))
            {
                AssetHandle<TrackingAsset> handle = loader.Load(new AssetId<TrackingAsset>(guid));
                Exception error = await Assert.ThrowsAnyAsync<Exception>(
                    () => loader.WaitAsync(handle).AsTask());
                if (mode == TrackingLoadMode.ReturnDifferentRoot)
                    Assert.Contains("document root itself", error.Message);
                else if (mode == TrackingLoadMode.ReturnWithoutOpen)
                    Assert.Contains("open exactly one typed asset document", error.Message);
                else
                    Assert.IsType<InvalidDataException>(error);
            }

            Assert.Equal(expectedDisposals, TrackingAsset.DisposeCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal sealed class ProbeLoadState
    {
        internal int LoadCount;
    }

    private sealed class ThirdPartyStorage(AssetEntry entry, string path) : IAssetStorage
    {
        public bool TryFind(AssetGuid assetGuid, out AssetEntry result)
        {
            if (assetGuid == entry.AssetGuid)
            {
                result = entry;
                return true;
            }

            result = default;
            return false;
        }

        public ValueTask<IRangeSource> OpenAsync(
            AssetEntry requested,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requested != entry)
                throw new InvalidDataException("The entry does not belong to this storage publication.");
            return ValueTask.FromResult<IRangeSource>(FileRangeSource.Open(path));
        }
    }

    private sealed class MutableThirdPartyStorage : IAssetStorage
    {
        private readonly object _gate = new();
        private AssetEntry _entry;
        private string? _path;

        internal void Publish(AssetEntry entry, string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            lock (_gate)
            {
                _entry = entry;
                _path = path;
            }
        }

        public bool TryFind(AssetGuid assetGuid, out AssetEntry result)
        {
            lock (_gate)
            {
                if (_path is not null && assetGuid == _entry.AssetGuid)
                {
                    result = _entry;
                    return true;
                }
            }

            result = default;
            return false;
        }

        public ValueTask<IRangeSource> OpenAsync(
            AssetEntry requested,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_path is null || requested != _entry)
                {
                    throw new InvalidDataException(
                        "The entry is no longer the current third-party storage publication.");
                }
                return ValueTask.FromResult<IRangeSource>(FileRangeSource.Open(_path));
            }
        }
    }

    public enum TrackingLoadMode
    {
        ReturnDifferentRoot,
        ReturnWithoutOpen,
        ThrowAfterTransfer,
    }

    internal readonly record struct TrackingLoadOptions(TrackingLoadMode Mode);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Parent, WeakReference Child)>
        LoadDependencyGraphAndReleaseAsync(
            AssetLoader loader,
            AssetGuid parentGuid,
            AssetGuid childGuid)
    {
        AssetHandle<ParentProbe> parent = loader.Load(new AssetId<ParentProbe>(parentGuid));
        await loader.WaitAsync(parent);
        Assert.Equal(1, parent.Reference!.DependencyCount);
        Assert.True(loader.TryFind(childGuid, out AssetHandle<ChildProbe> child));
        Assert.Equal(AssetLoadState.Ready, child.LoadState);
        return (new WeakReference(parent.Reference), new WeakReference(child.Reference!));
    }
}

[Asset(".probe.asset")]
[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ProbeAsset
{
    private static int s_encodingCount;

    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public int Value { get; set; }

    internal static int EncodingCount => Volatile.Read(ref s_encodingCount);
    internal static void ResetEncodingCount() => Volatile.Write(ref s_encodingCount, 0);

    internal static BinaryDocumentWriter CreateWriter(ProbeAsset asset)
    {
        Interlocked.Increment(ref s_encodingCount);
        return BinaryDocumentWriter.Create(asset);
    }

    internal static async ValueTask<ProbeAsset> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BinaryDocument<ProbeAsset> document = await context
            .OpenAsync<ProbeAsset>()
            .ConfigureAwait(false);
        AssetTypeExtensibilityTests.ProbeLoadState state = context.GetOptions(
            new AssetTypeExtensibilityTests.ProbeLoadState());
        Interlocked.Increment(ref state.LoadCount);
        return document.Root;
    }
}

[Asset(".tracking.asset")]
[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class TrackingAsset : IAsyncDisposable
{
    private static int s_disposeCount;
    private BinaryDocument<TrackingAsset>? _document;

    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public int Value { get; set; }

    internal static int DisposeCount => Volatile.Read(ref s_disposeCount);
    internal static void Reset() => Volatile.Write(ref s_disposeCount, 0);

    internal static async ValueTask<TrackingAsset> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssetTypeExtensibilityTests.TrackingLoadOptions options = context.GetOptions(
            new AssetTypeExtensibilityTests.TrackingLoadOptions(
                AssetTypeExtensibilityTests.TrackingLoadMode.ReturnDifferentRoot));
        if (options.Mode == AssetTypeExtensibilityTests.TrackingLoadMode.ReturnWithoutOpen)
        {
            return new TrackingAsset
            {
                AssetGuid = context.AssetGuid.ToFlatString(),
                Name = "unopened",
                Value = 0,
            };
        }

        BinaryDocument<TrackingAsset> document = await context
            .OpenAsync<TrackingAsset>()
            .ConfigureAwait(false);
        TrackingAsset owner = document.Root;
        owner._document = document;
        _ = context.Transfer(document, owner);
        if (options.Mode == AssetTypeExtensibilityTests.TrackingLoadMode.ThrowAfterTransfer)
            throw new InvalidDataException("failure after ownership transfer");
        return new TrackingAsset
        {
            AssetGuid = owner.AssetGuid,
            Name = owner.Name,
            Value = owner.Value,
        };
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref s_disposeCount);
        BinaryDocument<TrackingAsset>? document = Interlocked.Exchange(ref _document, null);
        if (document is not null)
            await document.DisposeAsync();
    }
}

[Asset(".childprobe.asset")]
[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ChildProbe : IDisposable
{
    public string? AssetGuid { get; set; }
    public string? Name { get; set; }

    public void Dispose()
    {
        if (Interlocked.Increment(ref DependencyRetirementOrder.ChildDisposals) == 1)
            DependencyRetirementOrder.Child = DependencyRetirementOrder.Next();
    }
}

[Asset(".parentprobe.asset")]
[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ParentProbe : IDisposable
{
    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public string? ChildGuid { get; set; }

    internal static async ValueTask<ParentProbe> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        BinaryDocument<ParentProbe> document = await context
            .OpenAsync<ParentProbe>()
            .ConfigureAwait(false);
        ParentProbe root = document.Root;
        SomeEngine.Assets.AssetGuid child = SomeEngine.Assets.AssetGuid.Parse(
            root.ChildGuid ?? throw new InvalidDataException("Parent child GUID is missing."));
        _ = await context.LoadDependencyAsync(new AssetId<ChildProbe>(child)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return root;
    }

    public void Dispose()
    {
        if (Interlocked.Increment(ref DependencyRetirementOrder.ParentDisposals) == 1)
            DependencyRetirementOrder.Parent = DependencyRetirementOrder.Next();
    }
}

internal static class DependencyRetirementOrder
{
    private static int s_sequence;
    internal static int Parent;
    internal static int Child;
    internal static int ParentDisposals;
    internal static int ChildDisposals;

    internal static int Next() => Interlocked.Increment(ref s_sequence);

    internal static void Reset()
    {
        Volatile.Write(ref s_sequence, 0);
        Volatile.Write(ref Parent, 0);
        Volatile.Write(ref Child, 0);
        Volatile.Write(ref ParentDisposals, 0);
        Volatile.Write(ref ChildDisposals, 0);
    }
}
