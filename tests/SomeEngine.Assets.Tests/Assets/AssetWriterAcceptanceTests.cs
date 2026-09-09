using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class AssetWriterAcceptanceTests
{
    [Fact]
    public void ProjectEncodesDiskFieldsOnce()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        CountingFields.ResetCounts();
        var fields = new CountingFields { Name = "one encode" };
        using Asset<CountedObject> asset = fields.ToAsset();
        Guid id = project.CreateAsset("one.counting.asset", asset);
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(1, fields.WriterCreations);
        Assert.Equal(1, fields.WriteCalls);
    }

    [Fact]
    public void InvalidDependenciesLeaveExistingFileAndIndexUntouched()
    {
        using var directory = new AssetTestDirectory();
        string path = directory.File("invalid.counting.asset");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(path, original);
        var project = new AssetProject(directory.Path, []);
        CountingFields.ResetCounts();
        var fields = new CountingFields { InvalidDependencies = true };
        Assert.Throws<InvalidDataException>(() => project.CreateAsset(path, fields.ToAsset()));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(project.Manifest.Assets);
    }

    [Fact]
    public void WrongWriterRootIsRejectedBeforeEncodingAndReplacingFile()
    {
        using var directory = new AssetTestDirectory();
        string path = directory.File("wrong.counting.asset");
        byte[] original = [8, 9];
        File.WriteAllBytes(path, original);
        CountingFields.ResetCounts();
        var fields = new CountingFields { Name = "wrong root", UseWrongRoot = true };
        using Asset<CountedObject> asset = fields.ToAsset();
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write(asset, path));
        Assert.Equal(1, fields.WriterCreations);
        Assert.Equal(0, fields.WriteCalls);
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}

public sealed class CountedObject;

[Asset(typeof(CountedObject), ".counting.asset")]
public sealed partial class CountingFields : IBinaryContract<CountingFields>
{
    private static int _writerCreations;
    private static int _writeCalls;

    internal CountingFields CopyFields() => (CountingFields)MemberwiseClone();
    public static void ResetCounts() { _writerCreations = 0; _writeCalls = 0; }
    public static Guid TypeId { get; } = Guid.Parse("e21114ba-02b5-44c5-9828-635c4890a28c");
    public static ulong SchemaFingerprint => 0x80DFE1B436B98B11UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;
    public string? AssetGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool UseWrongRoot { get; set; }
    public bool InvalidDependencies { get; set; }
    internal IReadOnlyList<Guid> GetDependencies(string path) => InvalidDependencies
        ? throw new InvalidDataException("Invalid dependency declaration.") : [];
    public int WriterCreations => Volatile.Read(ref _writerCreations);
    public int WriteCalls => Volatile.Read(ref _writeCalls);

    public static BinaryDocumentWriter CreateWriter(CountingFields asset)
    {
        Interlocked.Increment(ref _writerCreations);
        return asset.UseWrongRoot
            ? BinaryDocumentWriter.Create(new ReadingSampleData
            {
                AssetGuid = asset.AssetGuid,
                Name = "wrong contract",
            })
            : BinaryDocumentWriter.Create(asset);
    }

    public static void Write(ref BinaryDataWriter writer, CountingFields value)
    {
        Interlocked.Increment(ref _writeCalls);
        writer.WriteString(value.AssetGuid);
        writer.WriteString(value.Name);
    }

    public static CountingFields Read(ref BinaryDataReader reader)
        => new()
        {
            AssetGuid = reader.ReadString(),
            Name = reader.ReadString() ?? string.Empty,
        };
}
