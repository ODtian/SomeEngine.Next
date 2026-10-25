using SomeEngine.Assets.Tests.Assets;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets.Tests;

public sealed class AssetContentSelectionTests
{
    [Fact]
    public async Task Explicit_pack_order_selects_the_first_asset_without_mixing_sources()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        string first = directory.WriteSample(id, 11, "first");
        string second = directory.WriteSample(id, 22, "second");
        await new AssetPackBuilder().AddAsset(id, ReadingSampleAsset.FileType,
            await File.ReadAllBytesAsync(first), ReadingSampleAsset.SchemaHash).PublishAsync(directory.File("first.pack"));
        await new AssetPackBuilder().AddAsset(id, ReadingSampleAsset.FileType,
            await File.ReadAllBytesAsync(second), ReadingSampleAsset.SchemaHash).PublishAsync(directory.File("second.pack"));

        await using AssetContent preferredFirst = await AssetContent.OpenAsync(directory.Path,
            ["first.pack", "second.pack"], CancellationToken.None);
        await using AssetContent preferredSecond = await AssetContent.OpenAsync(directory.Path,
            ["second.pack", "first.pack"], CancellationToken.None);
        await using ReadingSampleAsset firstAsset = await ReadingSampleAsset.OpenAsync(preferredFirst.Loader, new(id));
        await using ReadingSampleAsset secondAsset = await ReadingSampleAsset.OpenAsync(preferredSecond.Loader, new(id));
        ReadingSampleBlock firstBlock = await firstAsset.GetBlockAsync<ReadingSampleBlock>();
        ReadingSampleBlock secondBlock = await secondAsset.GetBlockAsync<ReadingSampleBlock>();
        Assert.Equal(11, firstBlock.Number);
        Assert.Equal(22, secondBlock.Number);
        Assert.True(preferredFirst.MatchesSource(directory.Path,
            [directory.File("first.pack"), directory.File("second.pack")]));
        Assert.False(preferredFirst.MatchesSource(directory.Path, ["second.pack", "first.pack"]));
        Assert.False(preferredFirst.MatchesSource(directory.Path, []));
    }

    [Fact]
    public async Task A_direct_pack_path_and_the_same_explicit_selection_have_the_same_source()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        string asset = directory.WriteSample(id, 7);
        string path = directory.File("content.pack");
        await new AssetPackBuilder().AddAsset(id, ReadingSampleAsset.FileType,
            await File.ReadAllBytesAsync(asset), ReadingSampleAsset.SchemaHash).PublishAsync(path);
        await using AssetContent content = await AssetContent.OpenAsync(path);
        Assert.True(content.MatchesSource(path, []));
        Assert.True(content.MatchesSource(directory.Path, ["content.pack"]));
        Assert.True(AssetContent.Exists(path));
        Assert.True(AssetContent.Exists(directory.Path, ["content.pack"]));
        await using ReadingSampleAsset file = await ReadingSampleAsset.OpenAsync(content.Loader, new(id));
        Assert.Equal(7, (await file.GetBlockAsync<ReadingSampleBlock>()).Number);
    }

    [Fact]
    public async Task Failed_later_pack_open_releases_the_earlier_pack_for_exclusive_access()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        string asset = directory.WriteSample(id, 7);
        string path = directory.File("content.pack");
        await new AssetPackBuilder().AddAsset(id, ReadingSampleAsset.FileType,
            await File.ReadAllBytesAsync(asset), ReadingSampleAsset.SchemaHash).PublishAsync(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => AssetContent.OpenAsync(directory.Path,
            ["content.pack", "missing.pack"], CancellationToken.None).AsTask());
        using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task Adding_a_pack_list_invalidates_content_opened_from_the_directory_catalog()
    {
        using var directory = new AssetTestDirectory();
        await using AssetContent content = await AssetContent.OpenAsync(directory.Path);
        Assert.False(content.HasChanges);
        AssetContent.WritePackPaths(directory.Path, "selected.pack");
        Assert.True(content.HasChanges);
    }
}
