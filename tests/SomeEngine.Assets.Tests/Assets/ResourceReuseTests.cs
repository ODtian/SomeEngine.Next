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
        var values = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        SessionDesc settings = ShaderSettings();

        Shader first = await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings);
        Shader second = await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings);

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
        var values = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        SessionDesc settings = ShaderSettings();
        Task<Shader> first = ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Shader>[] others = Enumerable.Range(0, 31).Select(_ =>
            ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings).AsTask()).ToArray();
        Assert.Equal(1, storage.Opens);
        storage.ReleaseReads();
        Shader expected = await first;
        Shader[] results = await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.Same(expected, result));
        Assert.Equal(1, storage.Opens);
    }

    [Fact]
    public async Task DifferentFileSourcesCreateTheirOwnShaders()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var firstFiles = new AssetLoader(storage);
        await using var secondFiles = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        SessionDesc settings = ShaderSettings();

        Shader first = await ShaderAsset.GetAsync(firstFiles, id, values, SlangShaderImporter.GlobalSession, settings);
        Shader second = await ShaderAsset.GetAsync(secondFiles, id, values, SlangShaderImporter.GlobalSession, settings);

        Assert.NotSame(first, second);
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
        var values = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        SessionDesc settings = ShaderSettings();
        Shader first = await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings);

        await WriteShaderAsync(directory, storage, id.Value);
        Shader second = await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings);

        Assert.NotSame(first, second);
        Assert.True(first.TryGetSlangEntry(0, "Compute", SlangStage.Compute, out EntryPointReflection oldEntry));
        Assert.True(first.GetSlangEntryBlob(0, oldEntry)!.GetBufferSize() > 0);
        Assert.Same(second, await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings));
        Assert.Equal(2, storage.Opens);
    }

    [Fact]
    public async Task CollectedShaderIsCreatedAgainFromTheSameSource()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var files = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        SessionDesc settings = ShaderSettings();
        WeakReference<Shader> first = GetCollectibleShader(files, id, values, settings);
        Collect();
        Assert.False(first.TryGetTarget(out _));

        Shader second = await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings);

        Assert.True(second.TryGetSlangEntry(0, "Compute", SlangStage.Compute, out EntryPointReflection entry));
        Assert.True(second.GetSlangEntryBlob(0, entry)!.GetBufferSize() > 0);
        Assert.Same(second, await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings));
    }

    [Fact]
    public async Task ClosingTheFileReaderKeepsAcquiredShaderCodeUsable()
    {
        using var directory = new AssetTestDirectory();
        await using var storage = new ReadingStorage();
        AssetId<ShaderAsset> id = await WriteShaderAsync(directory, storage);
        await using var files = new AssetLoader(storage);
        var values = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        Shader shader = await ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, ShaderSettings());

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
        AssetTestDirectory directory, ReadingStorage storage, Guid id = default)
    {
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        Assert.True(slang.CreateSession(ShaderSettings(), out ISession session).Succeeded);
        ISlangBlob text = Slang.CreateBlob(Encoding.UTF8.GetBytes("""
            RWStructuredBuffer<uint> Values;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Compute(uint3 thread : SV_DispatchThreadID) { Values[thread.x] = thread.x; }
            """));
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
        WeakValues<AssetResourceKey<ShaderAsset>, Shader> values, SessionDesc settings)
        => new(ShaderAsset.GetAsync(files, id, values, SlangShaderImporter.GlobalSession, settings).AsTask().GetAwaiter().GetResult());

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
