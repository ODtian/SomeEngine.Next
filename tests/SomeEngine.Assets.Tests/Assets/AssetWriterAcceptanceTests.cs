using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class AssetWriterAcceptanceTests
{
    [Fact]
    public void AssetProjectCreatesAndEncodesTheAssetExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        var asset = new CountingAsset { Name = "one encode" };

        AssetGuid guid = project.CreateAsset(
            "assets/one.counting.asset",
            asset);

        Assert.False(guid.IsEmpty);
        Assert.Equal(1, asset.WriterCreations);
        Assert.Equal(1, asset.WriteCalls);
    }

    [Fact]
    public void InvalidDependenciesFailBeforeReplacingTheAssetFile()
    {
        using var directory = new TemporaryDirectory();
        const string relativePath = "assets/invalid.material.asset";
        string fullPath = Path.Combine(
            directory.Path,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        byte[] original = [0x41, 0x53, 0x53, 0x45, 0x54];
        File.WriteAllBytes(fullPath, original);
        var project = new AssetProject(directory.Path, []);
        var asset = new Material
        {
            Name = "invalid",
            Passes =
            [
                new PassEntry
                {
                    Shader = new ShaderRef
                    {
                        AssetGuid = "not-a-guid",
                        EntryPoint = "main",
                        Stage = ShaderStage.Compute,
                    },
                },
            ],
            Textures = [],
            Scalars = [],
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            project.CreateAsset(relativePath, asset));

        Assert.Contains("Passes[0].Shader.AssetGuid", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(fullPath));
        Assert.Empty(project.Manifest.Assets);

        Assert.Throws<InvalidDataException>(() => AssetWriter.Write(asset, fullPath));
        Assert.Equal(original, File.ReadAllBytes(fullPath));
    }

    [Fact]
    public void WriterWithAnotherRootContractFailsBeforeEncodingOrWriting()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        var asset = new CountingAsset { Name = "wrong writer root", UseWrongRoot = true };
        const string relativePath = "assets/wrong.counting.asset";
        string fullPath = Path.Combine(
            directory.Path,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            project.CreateAsset(relativePath, asset));

        Assert.Contains("instead of exact contract", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, asset.WriterCreations);
        Assert.Equal(0, asset.WriteCalls);
        Assert.False(File.Exists(fullPath));
        Assert.Empty(project.Manifest.Assets);
    }

    [Asset(".counting.asset")]
    internal sealed class CountingAsset : IBinaryContract<CountingAsset>
    {
        private int _writerCreations;
        private int _writeCalls;

        public static Guid TypeId { get; } = Guid.Parse("e21114ba-02b5-44c5-9828-635c4890a28c");
        public static ulong SchemaFingerprint => 0x80DFE1B436B98B11UL;
        public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
        public static uint SchemaEpoch => 1;
        public string? AssetGuid { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool UseWrongRoot { get; set; }
        public int WriterCreations => Volatile.Read(ref _writerCreations);
        public int WriteCalls => Volatile.Read(ref _writeCalls);

        public static BinaryDocumentWriter CreateWriter(CountingAsset asset)
        {
            Interlocked.Increment(ref asset._writerCreations);
            return asset.UseWrongRoot
                ? BinaryDocumentWriter.Create(new Material
                {
                    AssetGuid = asset.AssetGuid,
                    Name = "wrong contract",
                    Passes = [],
                    Textures = [],
                    Scalars = [],
                })
                : BinaryDocumentWriter.Create(asset);
        }

        public static void Write(ref BinaryDataWriter writer, CountingAsset value)
        {
            Interlocked.Increment(ref value._writeCalls);
            writer.WriteString(value.AssetGuid);
            writer.WriteString(value.Name);
        }

        public static CountingAsset Read(ref BinaryDataReader reader)
            => new()
            {
                AssetGuid = reader.ReadString(),
                Name = reader.ReadString() ?? string.Empty,
            };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-AssetWriter-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
