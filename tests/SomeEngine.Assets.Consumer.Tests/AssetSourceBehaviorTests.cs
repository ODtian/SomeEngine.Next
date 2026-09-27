using System.Text;
using System.Text.Json;
using SomeEngine.Examples.Curves;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class AssetSourceBehaviorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "someengine-asset-source-" + Guid.NewGuid().ToString("N"));
    public AssetSourceBehaviorTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task EditableSourceRestoresIdentityAndBuildsThroughBinaryDocument()
    {
        string sourcePath = Path.Combine(_directory, "speed.curve");
        string binaryPath = Path.Combine(_directory, "speed.curve.asset");
        using var original = new CurveAsset { Name = "speed" };
        original.GetBlock<CurveBlock>().Points = [new(0, 1), new(1, 5)];
        await original.SaveSourceAsync(sourcePath);
        byte[] text = await File.ReadAllBytesAsync(sourcePath);
        AssetEntry description = AssetText.Describe(text);
        Assert.Equal(original.Id, description.AssetGuid);
        Assert.Equal("speed", description.Name);
        using var pages = new PageArena(new ResidencyBudgetLedger());
        using CurveAsset source = await CurveAsset.OpenSourceAsync(sourcePath, pages);
        Assert.Same(pages, source.Pages);
        Assert.Equal(original.Id, source.Id);
        source.GetBlock<CurveBlock>().Points[1] = new(1, 9);
        await source.SaveSourceAsync(sourcePath);
        await source.SaveAsync(binaryPath);
        using CurveAsset binary = await CurveAsset.OpenAsync(binaryPath);
        using Curve curve = binary.GetBlock<CurveBlock>().CreateCurve();
        Assert.Equal(5, curve.Evaluate(0.5f));
        binary.Dispose();
        File.Delete(binaryPath);
        using CurveAsset reopened = await CurveAsset.OpenSourceAsync(sourcePath);
        await reopened.SaveAsync(binaryPath);
        Assert.Equal(original.Id, reopened.Id);
        Assert.Equal(new Point(1, 9), reopened.GetBlock<CurveBlock>().Points[1]);
    }

    [Fact]
    public async Task UnknownTypeCanBeDescribedWithoutItsDecoder()
    {
        Guid id = Guid.NewGuid();
        string text = $$$"""
        {"Format":"SomeEngine.AssetSource.2","TypeId":"{{{Guid.NewGuid()}}}","Schema":"0000000000000001",
         "Fields":{"Id":"{{{id}}}","Name":"third-party","AssetType":"External.Uninstalled"},
         "Blocks":[{"TypeId":"{{{Guid.NewGuid()}}}","Schema":"0000000000000002","Fields":{"Arbitrary":{"Data":[1,2]}},"Chunks":{}}]}
        """;
        AssetEntry description = AssetText.Describe(Encoding.UTF8.GetBytes(text));
        Assert.Equal(id, description.AssetGuid);
        Assert.Equal("External.Uninstalled", description.AssetType);
        string path = Path.Combine(_directory, "unknown.curve");
        await File.WriteAllTextAsync(path, text);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await CurveAsset.OpenSourceAsync(path));
    }

    [Fact]
    public async Task PayloadReferencesStayBinaryAndSurviveSourceClosure()
    {
        byte[] bytes = Enumerable.Range(0, 180000).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_directory, "data.bin"), bytes);
        string path = Path.Combine(_directory, "data.payload");
        using var asset = new TextPayloadAsset();
        using var content = new TextPayloadBlock { Key = 7, Length = (ulong)bytes.Length };
        asset.SetBlock(content);
        await asset.SaveSourceAsync(path, new Dictionary<ulong, string> { [7] = "data.bin" });
        Assert.True(new FileInfo(path).Length < 1000);
        using var source = await TextPayloadAsset.OpenSourceAsync(path);
        TextPayloadBlock sourceContent = source.GetBlock<TextPayloadBlock>();
        using var acquired = await sourceContent.AcquireBufferAsync(sourceContent.Key);
        source.Dispose();
        File.Delete(Path.Combine(_directory, "data.bin"));
        using var read = acquired.Retain();
        Assert.Equal(bytes, read.ToArray());
        using var pages = new PageArena(new ResidencyBudgetLedger());
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await TextPayloadAsset.OpenSourceAsync(path, pages));
        Assert.Equal(0, pages.Statistics.ActiveBytes);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "first.bin"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "second.bin"), bytes);
        await asset.SaveSourceAsync(path, new Dictionary<ulong, string> { [7] = "first.bin", [8] = "second.bin" });
        File.Delete(Path.Combine(_directory, "second.bin"));
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await TextPayloadAsset.OpenSourceAsync(path, pages));
        Assert.Equal(0, pages.Statistics.ActiveBytes);
    }

    [Fact]
    public async Task AnotherIdentityCannotReplaceAnExistingSourceAndFailedSaveLeavesNoTemporaryFile()
    {
        string path = Path.Combine(_directory, "curve.source");
        using var first = new CurveAsset { Name = "first" };
        first.GetBlock<CurveBlock>().Points = [new(0, 1)];
        await first.SaveSourceAsync(path);
        byte[] before = await File.ReadAllBytesAsync(path);
        using var second = first.CreateCopy();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await second.SaveSourceAsync(path));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UnloadedBinaryChunksNeedExplicitSourceReferences()
    {
        string binaryPath = Path.Combine(_directory, "data.payload.asset");
        string sourcePath = Path.Combine(_directory, "data.payload");
        byte[] bytes = [3, 7, 9];
        using var asset = new TextPayloadAsset();
        using var content = new TextPayloadBlock { Key = 7, Length = 3 };
        content.Chunks.Set(7, bytes);
        asset.SetBlock(content);
        await asset.SaveAsync(binaryPath);
        using var binary = await TextPayloadAsset.OpenAsync(binaryPath);
        TextPayloadBlock binaryContent = await binary.GetBlockAsync<TextPayloadBlock>();
        Assert.Empty(binaryContent.Chunks.Keys);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await binary.SaveSourceAsync(sourcePath));
        Assert.False(File.Exists(sourcePath));
        await File.WriteAllBytesAsync(Path.Combine(_directory, "data.bin"), bytes);
        await binary.SaveSourceAsync(sourcePath, new Dictionary<ulong, string> { [7] = "data.bin" });
        using var source = await TextPayloadAsset.OpenSourceAsync(sourcePath);
        using var read = source.GetBlock<TextPayloadBlock>().Chunks.Retain(7);
        Assert.Equal(bytes, read.ToArray());
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Name")]
    [InlineData("AssetType")]
    public async Task ASourceMustContainItsPublicIdentity(string missing)
    {
        string path = Path.Combine(_directory, "missing.curve");
        using var asset = new CurveAsset();
        await asset.SaveSourceAsync(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Fields"]!.AsObject().Remove(missing);
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(async () => await CurveAsset.OpenSourceAsync(path));
    }
}

[Asset(".payload.asset"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class TextPayloadAsset : Asset
{
    public new System.Collections.Immutable.ImmutableArray<AssetBlockDescription> BlockDescriptions
    { get => base.BlockDescriptions; private set => base.BlockDescriptions = value; }
    public async ValueTask<AssetEntry> SaveSourceAsync(string path, IReadOnlyDictionary<ulong, string>? chunkFiles = null,
        CancellationToken cancellationToken = default)
    {
        await GetBlockAsync<TextPayloadBlock>(cancellationToken);
        return await AssetText.SaveAsync(path, this, (writer, asset) => writer.WriteBlock(asset.GetBlock<TextPayloadBlock>(), chunkFiles), cancellationToken);
    }
    public static async ValueTask<TextPayloadAsset> OpenSourceAsync(string path, PageArena? pages = null, CancellationToken cancellationToken = default)
    {
        using AssetText.SourceReader source = await AssetText.OpenAsync(path, cancellationToken);
        source.RequireAsset<TextPayloadAsset>(FileType);
        var asset = new TextPayloadAsset(pages ?? PageArena.Shared, source.Description.AssetGuid) { Name = source.Description.Name };
        try
        {
            using var block = new TextPayloadBlock(asset.Pages);
            await source.ReadBlockIntoAsync(block, cancellationToken);
            asset.SetBlock(block);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
}
[AssetBlock, BinarySerializable, TextSerializable, BinaryFields(nameof(Key), nameof(Length))]
public sealed partial class TextPayloadBlock : AssetPageBlock
{
    public partial ulong Key { get; set; }
    public partial ulong Length { get; set; }
}
