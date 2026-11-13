using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Core.Collections;
using SomeEngine.Graphics;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ResourceReuseTests
{
    [Fact]
    public async Task LiveTextureIsReturnedByRepeatedAcquisition()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<TextureAsset> id = await WriteTextureAsync(directory, storage, 17);
        await using var files = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();

        Texture first = await TextureAsset.GetAsync(files, id, values);
        Texture second = await TextureAsset.GetAsync(files, id, values);

        Assert.Same(first, second);
        Assert.Equal(1, storage.Opens);
    }

    [Fact]
    public async Task ConcurrentTextureAcquisitionsShareTheCompleteCreation()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage(block: true);
        AssetId<TextureAsset> id = await WriteTextureAsync(directory, storage, 18);
        await using var files = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();
        Task<Texture> first = TextureAsset.GetAsync(files, id, values).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Texture>[] others = Enumerable.Range(0, 31)
            .Select(_ => TextureAsset.GetAsync(files, id, values).AsTask()).ToArray();
        Assert.Equal(1, storage.Opens);
        storage.ReleaseReads();
        Texture expected = await first;
        Texture[] results = await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.Same(expected, result));
        Assert.Equal(1, storage.Opens);
    }

    [Fact]
    public async Task DifferentFileSourcesCreateTheirOwnTextures()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<TextureAsset> id = await WriteTextureAsync(directory, storage, 19);
        await using var firstFiles = new AssetLoader(storage);
        await using var secondFiles = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();

        Texture first = await TextureAsset.GetAsync(firstFiles, id, values);
        Texture second = await TextureAsset.GetAsync(secondFiles, id, values);

        Assert.NotSame(first, second);
        Assert.Same(first, await TextureAsset.GetAsync(firstFiles, id, values));
        Assert.Same(second, await TextureAsset.GetAsync(secondFiles, id, values));
        Assert.Equal(2, storage.Opens);
    }

    [Fact]
    public async Task NewTextureContentCreatesItsOwnInstanceWhileTheOldValueRemainsUsable()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<TextureAsset> id = await WriteTextureAsync(directory, storage, 20);
        await using var files = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();
        Texture first = await TextureAsset.GetAsync(files, id, values);

        await WriteTextureAsync(directory, storage, 21, id.Value);
        Texture second = await TextureAsset.GetAsync(files, id, values);

        Assert.NotSame(first, second);
        Assert.True(first.TryGetTile(first.MipTiles[0].ChunkKey, out ReadOnlyMemory<byte> oldPixels));
        Assert.True(second.TryGetTile(second.MipTiles[0].ChunkKey, out ReadOnlyMemory<byte> newPixels));
        Assert.Equal(new byte[] { 20 }, oldPixels.ToArray());
        Assert.Equal(new byte[] { 21 }, newPixels.ToArray());
        Assert.Same(second, await TextureAsset.GetAsync(files, id, values));
        Assert.Equal(2, storage.Opens);
    }

    [Fact]
    public async Task CollectedTextureIsCreatedAgainFromTheSameSource()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<TextureAsset> id = await WriteTextureAsync(directory, storage, 22);
        await using var files = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();
        WeakReference<Texture> first = GetCollectibleTexture(files, id, values);
        Collect();
        Assert.False(first.TryGetTarget(out _));

        Texture second = await TextureAsset.GetAsync(files, id, values);

        Assert.True(second.TryGetTile(second.MipTiles[0].ChunkKey, out ReadOnlyMemory<byte> pixels));
        Assert.Equal(new byte[] { 22 }, pixels.ToArray());
        Assert.Same(second, await TextureAsset.GetAsync(files, id, values));
    }

    [Fact]
    public async Task ClosingTheFileReaderKeepsAcquiredTexturePixelsUsable()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<TextureAsset> id = await WriteTextureAsync(directory, storage, 23);
        await using var files = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();
        Texture texture = await TextureAsset.GetAsync(files, id, values);

        await files.DisposeAsync();

        Assert.Equal(1, storage.RangesClosed);
        Assert.True(texture.TryGetTile(texture.MipTiles[0].ChunkKey, out ReadOnlyMemory<byte> pixels));
        Assert.Equal(new byte[] { 23 }, pixels.ToArray());
    }

    [Fact]
    public async Task LiveShaderIsReturnedByRepeatedAcquisition()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var files = new AssetLoader(storage);
        using var library = new ShaderLibrary(SlangShaderImporter.GlobalSession, ShaderSettings());

        Shader first = await library.GetAsync(files, id);
        Shader second = await library.GetAsync(files, id);

        Assert.Same(first, second);
        Assert.Equal(1, storage.Opens);
    }

    [Fact]
    public async Task ConcurrentShaderAcquisitionsShareTheCompleteCreation()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage(block: true);
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var files = new AssetLoader(storage);
        using var library = new ShaderLibrary(SlangShaderImporter.GlobalSession, ShaderSettings());
        Task<Shader> first = library.GetAsync(files, id).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Shader>[] others = Enumerable.Range(0, 31).Select(_ =>
            library.GetAsync(files, id).AsTask()).ToArray();
        Assert.Equal(1, storage.Opens);
        storage.ReleaseReads();
        Shader expected = await first;
        Shader[] results = await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.Same(expected, result));
        Assert.Equal(1, storage.Opens);
    }

    [Fact]
    public async Task ShaderContentIsSharedAcrossFileReaders()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var firstFiles = new AssetLoader(storage);
        await using var secondFiles = new AssetLoader(storage);
        using var library = new ShaderLibrary(SlangShaderImporter.GlobalSession, ShaderSettings());

        Shader first = await library.GetAsync(firstFiles, id);
        Shader second = await library.GetAsync(secondFiles, id);

        Assert.Same(first, second);
        Assert.Equal(2, storage.Opens);
        GC.KeepAlive(first);
    }

    [Fact]
    public async Task NewShaderContentCreatesItsOwnInstanceWhileTheOldCodeRemainsUsable()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var files = new AssetLoader(storage);
        using var library = new ShaderLibrary(SlangShaderImporter.GlobalSession, ShaderSettings());
        Shader first = await library.GetAsync(files, id);

        await WriteShaderAsync(directory, storage, id.Value, addition: 1);
        await library.RefreshAsync();
        Shader second = await library.GetAsync(files, id);

        Assert.NotSame(first, second);
        Assert.True(first.TryGetSlangEntry(0, "Compute", SlangStage.Compute, out EntryPointReflection oldEntry));
        Assert.True(first.GetSlangEntryBlob(0, oldEntry)!.GetBufferSize() > 0);
        Assert.Same(second, await library.GetAsync(files, id));
        Assert.NotEqual(first.GetSlangEntryBlob(0, oldEntry)!.Buffer.ToArray(),
            second.GetSlangEntryBlob(0, second.RequireSlangEntry(0, "Compute", SlangStage.Compute))!.Buffer.ToArray());
        Assert.Equal(2, storage.Opens);
    }

    [Fact]
    public async Task ShaderLibraryRetainsAcquiredShaders()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var files = new AssetLoader(storage);
        using var library = new ShaderLibrary(SlangShaderImporter.GlobalSession, ShaderSettings());
        WeakReference<Shader> first = GetCollectibleShader(files, id, library);
        Collect();
        Assert.True(first.TryGetTarget(out Shader? retained));

        Shader second = await library.GetAsync(files, id);
        Assert.Same(retained, second);

        Assert.True(second.TryGetSlangEntry(0, "Compute", SlangStage.Compute, out EntryPointReflection entry));
        Assert.True(second.GetSlangEntryBlob(0, entry)!.GetBufferSize() > 0);
        Assert.Same(second, await library.GetAsync(files, id));
    }

    [Fact]
    public async Task ClosingTheFileReaderKeepsAcquiredShaderCodeUsable()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var files = new AssetLoader(storage);
        using var library = new ShaderLibrary(SlangShaderImporter.GlobalSession, ShaderSettings());
        Shader shader = await library.GetAsync(files, id);

        await files.DisposeAsync();

        Assert.Equal(1, storage.RangesClosed);
        Assert.True(shader.TryGetSlangEntry(0, "Compute", SlangStage.Compute, out EntryPointReflection entry));
        Assert.True(shader.GetSlangEntryBlob(0, entry)!.GetBufferSize() > 0);
    }

    private static async Task<AssetId<TextureAsset>> WriteTextureAsync(
        AssetTestDirectory directory, ReadingStorage storage, byte pixel, Guid id = default)
    {
        using var asset = new TextureAsset(id) { Name = "shared pixels" };
        using var content = new TextureBlock
        {
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Dimension = TextureDimension.Texture2D, Format = Format.R8UNorm,
            SampledFormat = Format.R8UNorm, SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile
            {
                Width = 1, Height = 1, RowPitch = 1, SlicePitch = 1, DecodedLength = 1,
                ChunkKey = TextureTiles.ChunkKey(0, 0, 0, 0, 0, 0)
            }]
        };
        content.SetBytes(content.MipTiles[0].ChunkKey, new byte[] { pixel });
        asset.SetBlock(content);
        string path = directory.File($"{asset.Id}-{pixel}.texture.asset");
        await asset.SaveAsync(path);
        storage.Publish(asset.Id, path, TextureAsset.FileType, TextureAsset.SchemaHash);
        return new(asset.Id);
    }

    private static async Task<AssetId<ShaderAsset>> WriteShaderAsync(
        AssetTestDirectory directory, ReadingStorage storage, Guid id = default, uint addition = 0)
    {
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        Assert.True(slang.CreateSession(ShaderSettings(), out ISession session).Succeeded);
        ISlangBlob text = Slang.CreateBlob(Encoding.UTF8.GetBytes("""
            RWStructuredBuffer<uint> Values;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Compute(uint3 thread : SV_DispatchThreadID) { Values[thread.x] = thread.x + ADDITIONu; }
            """.Replace("ADDITION", addition.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        IModule? input = session.LoadModuleFromSource("reuse", "reuse.slang", text, out ISlangBlob? diagnostics);
        Assert.True(input is not null, diagnostics?.AsString);
        Assert.True(input!.Serialize(out ISlangBlob serialized).Succeeded);
        using var asset = new ShaderAsset(id) { Name = "shared shader" };
        ShaderSourceBlock source = asset.GetBlock<ShaderSourceBlock>();
        source.SourceNames = ["reuse.slang"];
        source.SetSerializedSource(0, serialized.Buffer);
        string path = directory.File($"{asset.Id}-{Guid.NewGuid()}.shader.asset");
        await asset.SaveAsync(path);
        storage.Publish(asset.Id, path, ShaderAsset.FileType, ShaderAsset.SchemaHash);
        return new(asset.Id);
    }

    private static SessionDesc ShaderSettings() => new()
    {
        Targets = [new TargetDesc
        {
            Format = SlangCompileTarget.Spirv,
            Profile = SlangShaderImporter.GlobalSession.FindProfile("glsl_460")
        }]
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Texture> GetCollectibleTexture(AssetLoader files, AssetId<TextureAsset> id,
        WeakValues<AssetResourceKey<TextureAsset>, Texture> values)
        => new(TextureAsset.GetAsync(files, id, values).AsTask().GetAwaiter().GetResult());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Shader> GetCollectibleShader(AssetLoader files, AssetId<ShaderAsset> id,
        ShaderLibrary library)
        => new(library.GetAsync(files, id).AsTask().GetAwaiter().GetResult());

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
