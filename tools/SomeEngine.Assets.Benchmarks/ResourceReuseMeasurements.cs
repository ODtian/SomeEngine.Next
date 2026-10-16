using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Collections;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using Texture = SomeEngine.Resources.Texture;

internal static class ResourceReuseMeasurements
{
    public static async Task<int> RunAsync()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-ResourceReuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession slang).Failed)
                throw new InvalidOperationException("Slang session creation failed.");
            var settings = new SessionDesc
            {
                Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = slang.FindProfile("glsl_460") }]
            };
            Console.WriteLine($"resource-reuse dotnet={Environment.Version} tiered={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")} byte-budget=67108864 texture=1024x1024-R8 shader=one-compute-entry");
            var storage = new MeasuredStorage();
            AssetId<TextureAsset> textureId = await WriteTextureAsync(directory, storage, 71);
            AssetId<ShaderAsset> irId = await WriteShaderAsync(directory, storage, slang, settings, "ir");
            AssetId<ShaderAsset> codeId = await WriteCodeShaderAsync(directory, storage, slang, settings);
            var bytes = new AssetByteCache(64L * 1024 * 1024);
            await using (var files = new AssetLoader(storage, new() { ByteCache = bytes }))
            {
                var textures = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();
                var shaders = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
                WeakReference<Texture>[] oldTextures = MeasureTextureReferences(directory, storage, files, textures, textureId);
                Collect();
                RequireCollected(oldTextures);
                Texture rebuilt = Measure("texture-after-gap", storage,
                    () => TextureAsset.GetAsync(files, textureId, textures).GetAwaiter().GetResult());
                Console.WriteLine($"texture-creations total=3 overlap-peak=2 gap-old-collected={oldTextures.Length} rebuilt-pixel-bytes={PixelBytes(rebuilt)}");

                WeakReference<Shader>[] oldIr = MeasureShaderReferences(directory, storage, files, shaders, irId, slang, settings);
                Collect();
                RequireCollected(oldIr);
                Shader rebuiltIr = Measure("ir-after-gap", storage,
                    () => ShaderAsset.GetAsync(files, irId, shaders, slang, settings).GetAwaiter().GetResult());
                Console.WriteLine($"ir-creations total=3 overlap-peak=2 gap-old-collected={oldIr.Length} native-code-bytes={CodeBytes(rebuiltIr).Native} managed-code-bytes={CodeBytes(rebuiltIr).Managed}");

                WeakReference<Shader> oldCode = MeasureCodeReferences(storage, files, shaders, codeId, slang, settings);
                Collect();
                RequireCollected([oldCode]);
                Shader rebuiltCode = Measure("saved-code-after-gap", storage,
                    () => ShaderAsset.GetAsync(files, codeId, shaders, slang, settings).GetAwaiter().GetResult());
                Console.WriteLine($"saved-code-creations total=2 gap-old-collected=1 native-code-bytes={CodeBytes(rebuiltCode).Native} managed-code-bytes={CodeBytes(rebuiltCode).Managed}");
                Console.WriteLine($"retained byte-cache-items={bytes.CachedItems} byte-cache-arrays={bytes.CachedBytes} byte-cache-index={bytes.IndexBytes} weak-arrays=high-water");
                GC.KeepAlive(rebuilt);
                GC.KeepAlive(rebuiltIr);
                GC.KeepAlive(rebuiltCode);
            }

            var reopenedBytes = new AssetByteCache(64L * 1024 * 1024);
            await using (var files = new AssetLoader(storage, new() { ByteCache = reopenedBytes, MaxCachedFiles = 0 }))
            {
                var shaders = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
                WeakReference<Shader> oldCode = AcquireCollectibleCode(storage, files, shaders, codeId, slang, settings);
                Collect();
                RequireCollected([oldCode]);
                Shader code = Measure("saved-code-reopened-file", storage,
                    () => ShaderAsset.GetAsync(files, codeId, shaders, slang, settings).GetAwaiter().GetResult());
                Console.WriteLine($"reopened-file byte-cache-items={reopenedBytes.CachedItems} byte-cache-arrays={reopenedBytes.CachedBytes} byte-cache-index={reopenedBytes.IndexBytes} native-code-bytes={CodeBytes(code).Native} managed-code-bytes={CodeBytes(code).Managed}");
                GC.KeepAlive(code);
            }
            return 0;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Texture>[] MeasureTextureReferences(string directory, MeasuredStorage storage,
        AssetLoader files, WeakValues<AssetResourceKey<TextureAsset>, Texture> values, AssetId<TextureAsset> id)
    {
        Texture first = Measure("texture-cold", storage,
            () => TextureAsset.GetAsync(files, id, values).GetAwaiter().GetResult());
        Texture retained = Measure("texture-32-live-reads", storage, () =>
        {
            Texture value = first;
            for (int read = 0; read < 32; read++)
            {
                value = TextureAsset.GetAsync(files, id, values).Result;
                if (!ReferenceEquals(first, value)) throw new InvalidDataException("Live texture identity changed.");
            }
            return value;
        });
        Texture nextScene = Measure("texture-held-transition", storage,
            () => TextureAsset.GetAsync(files, id, values).GetAwaiter().GetResult());
        if (!ReferenceEquals(first, nextScene)) throw new InvalidDataException("Held texture identity changed.");
        _ = WriteTextureAsync(directory, storage, 72, id.Value).GetAwaiter().GetResult();
        Texture changed = Measure("texture-new-version", storage,
            () => TextureAsset.GetAsync(files, id, values).GetAwaiter().GetResult());
        if (ReferenceEquals(first, changed)) throw new InvalidDataException("Changed texture identity was reused.");
        Console.WriteLine($"texture-overlap objects=2 pixel-bytes={PixelBytes(first) + PixelBytes(changed)}");
        GC.KeepAlive(retained);
        return [new(first), new(changed)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Shader>[] MeasureShaderReferences(string directory, MeasuredStorage storage,
        AssetLoader files, WeakValues<AssetResourceKey<ShaderAsset>, Shader> values, AssetId<ShaderAsset> id,
        IGlobalSession slang, SessionDesc settings)
    {
        Shader first = Measure("ir-cold", storage,
            () => ShaderAsset.GetAsync(files, id, values, slang, settings).GetAwaiter().GetResult());
        Shader retained = Measure("ir-32-live-reads", storage, () =>
        {
            Shader value = first;
            for (int read = 0; read < 32; read++)
            {
                value = ShaderAsset.GetAsync(files, id, values, slang, settings).Result;
                if (!ReferenceEquals(first, value)) throw new InvalidDataException("Live shader identity changed.");
            }
            return value;
        });
        Shader nextScene = Measure("ir-held-transition", storage,
            () => ShaderAsset.GetAsync(files, id, values, slang, settings).GetAwaiter().GetResult());
        if (!ReferenceEquals(first, nextScene)) throw new InvalidDataException("Held shader identity changed.");
        _ = WriteShaderAsync(directory, storage, slang, settings, "ir", id.Value).GetAwaiter().GetResult();
        Shader changed = Measure("ir-new-version", storage,
            () => ShaderAsset.GetAsync(files, id, values, slang, settings).GetAwaiter().GetResult());
        if (ReferenceEquals(first, changed)) throw new InvalidDataException("Changed shader identity was reused.");
        Console.WriteLine($"ir-overlap objects=2 native-code-bytes={CodeBytes(first, changed).Native} managed-code-bytes={CodeBytes(first, changed).Managed}");
        GC.KeepAlive(retained);
        return [new(first), new(changed)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Shader> MeasureCodeReferences(MeasuredStorage storage, AssetLoader files,
        WeakValues<AssetResourceKey<ShaderAsset>, Shader> values, AssetId<ShaderAsset> id,
        IGlobalSession slang, SessionDesc settings)
    {
        Shader first = Measure("saved-code-cold", storage,
            () => ShaderAsset.GetAsync(files, id, values, slang, settings).GetAwaiter().GetResult());
        Shader second = Measure("saved-code-live", storage,
            () => ShaderAsset.GetAsync(files, id, values, slang, settings).GetAwaiter().GetResult());
        if (!ReferenceEquals(first, second)) throw new InvalidDataException("Live saved-code shader identity changed.");
        return new(first);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Shader> AcquireCollectibleCode(MeasuredStorage storage, AssetLoader files,
        WeakValues<AssetResourceKey<ShaderAsset>, Shader> values, AssetId<ShaderAsset> id,
        IGlobalSession slang, SessionDesc settings)
        => new(Measure("saved-code-first-reopened-file", storage,
            () => ShaderAsset.GetAsync(files, id, values, slang, settings).GetAwaiter().GetResult()));

    private static T Measure<T>(string name, MeasuredStorage storage, Func<T> read)
    {
        using var process = Process.GetCurrentProcess();
        long privateBefore = process.PrivateMemorySize64;
        Counts before = storage.Counts;
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        T value = read();
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Counts after = storage.Counts;
        process.Refresh();
        long privateChange = process.PrivateMemorySize64 - privateBefore;
        Console.WriteLine($"acquire name={name} elapsed={milliseconds:F3}ms managed={allocated}B process-private-change={privateChange}B opens={after.Opens - before.Opens} ir-reads={after.IrReads - before.IrReads} ir-bytes={after.IrBytes - before.IrBytes} code-reads={after.CodeReads - before.CodeReads} code-bytes={after.CodeBytes - before.CodeBytes} pixel-reads={after.PixelReads - before.PixelReads} pixel-bytes={after.PixelBytes - before.PixelBytes} source-bytes={after.SourceBytes - before.SourceBytes}");
        return value;
    }

    private static long PixelBytes(Texture texture)
    {
        long bytes = 0;
        foreach (TextureMipTile tile in texture.MipTiles)
            if (texture.TryGetTile(tile.ChunkKey, out ReadOnlyMemory<byte> pixels)) bytes += pixels.Length;
        return bytes;
    }

    private static unsafe (long Native, long Managed) CodeBytes(params Shader[] shaders)
    {
        long native = 0;
        long managed = 0;
        var addresses = new HashSet<nint>();
        foreach (Shader shader in shaders)
        {
            for (int target = 0; target < shader.SlangTargetCount; target++)
            {
                if (shader.GetSlangTargetBlob(target) is { } targetCode && addresses.Add((nint)targetCode.GetBufferPointer()))
                    native += targetCode.Buffer.Length;
                managed += shader.GetSlangTargetBytes(target).Length;
                for (int entry = 0; entry < shader.SlangEntryCount; entry++)
                {
                    if (shader.GetSlangEntryBlob(target, entry) is { } entryCode && addresses.Add((nint)entryCode.GetBufferPointer()))
                        native += entryCode.Buffer.Length;
                    managed += shader.GetSlangEntryBytes(target, entry).Length;
                }
            }
        }
        return (native, managed);
    }

    private static async Task<AssetId<TextureAsset>> WriteTextureAsync(string directory, MeasuredStorage storage,
        byte pixel, Guid id = default)
    {
        using var asset = new TextureAsset(id) { Name = "measured pixels" };
        TextureBlock block = asset.GetBlock<TextureBlock>();
        block.Dimension = TextureDimension.Texture2D;
        block.Width = block.Height = 1024;
        block.Depth = block.ArrayLayerCount = block.MipLevelCount = 1;
        block.Format = block.SampledFormat = Format.R8UNorm;
        block.SampledDimension = TextureViewDimension.Texture2D;
        var pixels = new byte[1024 * 1024];
        Array.Fill(pixels, pixel);
        TextureMipTile tile = TextureTiles.Describe(new TextureMipTile
            { Width = 1024, Height = 1024, RowPitch = 1024, SlicePitch = 1024 * 1024 }, pixels.Length);
        block.MipTiles = [tile];
        block.SetBytes(tile.ChunkKey, pixels);
        string path = Path.Combine(directory, $"{asset.Id}-{pixel}.texture.asset");
        AssetEntry entry = await asset.SaveAsync(path);
        await storage.AddAsync(entry, path, TextureBlock.BlockTypeId, PayloadKind.Pixels);
        return new(asset.Id);
    }

    private static async Task<AssetId<ShaderAsset>> WriteShaderAsync(string directory, MeasuredStorage storage,
        IGlobalSession slang, SessionDesc settings, string name, Guid id = default)
    {
        using ShaderAsset asset = CreateShader(slang, settings, name, id);
        string path = Path.Combine(directory, $"{asset.Id}-{Guid.NewGuid()}.shader.asset");
        AssetEntry entry = await asset.SaveAsync(path);
        await storage.AddAsync(entry, path, ShaderSourceBlock.BlockTypeId, PayloadKind.Ir);
        return new(asset.Id);
    }

    private static async Task<AssetId<ShaderAsset>> WriteCodeShaderAsync(string directory, MeasuredStorage storage,
        IGlobalSession slang, SessionDesc settings)
    {
        using ShaderAsset asset = CreateShader(slang, settings, "saved code");
        _ = await asset.CompileAsync(slang, settings);
        string path = Path.Combine(directory, $"{asset.Id}.shader.asset");
        AssetEntry entry = await asset.SaveAsync(path);
        await storage.AddAsync(entry, path, ShaderSourceBlock.BlockTypeId, PayloadKind.Ir, ShaderBlock.BlockTypeId);
        return new(asset.Id);
    }

    private static ShaderAsset CreateShader(IGlobalSession slang, SessionDesc settings, string name, Guid id = default)
    {
        if (slang.CreateSession(settings, out ISession session).Failed)
            throw new InvalidOperationException("Slang input session creation failed.");
        ISlangBlob text = Slang.CreateBlob(Encoding.UTF8.GetBytes("""
            RWStructuredBuffer<uint> Values;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Compute(uint3 thread : SV_DispatchThreadID) { Values[thread.x] = thread.x; }
            """));
        IModule input = session.LoadModuleFromSource("reuse", "reuse.slang", text, out _)!;
        if (input.Serialize(out ISlangBlob serialized).Failed)
            throw new InvalidOperationException("Slang input serialization failed.");
        var asset = new ShaderAsset(id) { Name = name };
        ShaderSourceBlock source = asset.GetBlock<ShaderSourceBlock>();
        source.SourceNames = ["reuse.slang"];
        source.SetSerializedSource(0, serialized.Buffer);
        return asset;
    }

    private static void RequireCollected<T>(WeakReference<T>[] values) where T : class
    {
        foreach (WeakReference<T> value in values)
            if (value.TryGetTarget(out _)) throw new InvalidDataException("The reference gap retained a resource.");
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private enum PayloadKind { Ir, Code, Pixels }
    private readonly record struct Payload(BinaryChunkEntry Chunk, PayloadKind Kind);
    private sealed record InputFile(AssetEntry Entry, string Path, Payload[] Data);
    private readonly record struct Counts(long Opens, long SourceBytes, long IrReads, long IrBytes,
        long CodeReads, long CodeBytes, long PixelReads, long PixelBytes);

    private sealed class MeasuredStorage : IAssetStorage
    {
        private readonly Dictionary<Guid, InputFile> _files = [];
        private long _opens, _sourceBytes, _irReads, _irBytes, _codeReads, _codeBytes, _pixelReads, _pixelBytes;
        public Counts Counts => new(_opens, _sourceBytes, _irReads, _irBytes, _codeReads, _codeBytes, _pixelReads, _pixelBytes);

        public async Task AddAsync(AssetEntry entry, string path, Guid dataBlock, PayloadKind kind, Guid codeBlock = default)
        {
            using AssetFile file = await AssetFile.OpenAsync(path, AssetHeader.WireType);
            var header = new AssetHeader();
            file.ReadRootInto(header);
            var data = new List<Payload>();
            foreach (AssetBlockDescription block in header.BlockDescriptions)
            {
                if (block.BlockTypeId != dataBlock && block.BlockTypeId != codeBlock) continue;
                foreach (ulong key in block.ChunkKeys)
                    data.Add(new(await file.DescribeChunkAsync(block.GetStorageKey(key)),
                        block.BlockTypeId == dataBlock ? kind : PayloadKind.Code));
            }
            _files[entry.AssetGuid] = new(entry, path, data.ToArray());
        }

        public bool TryFind(Guid assetGuid, out AssetEntry entry)
        {
            if (_files.TryGetValue(assetGuid, out InputFile? file)) { entry = file.Entry; return true; }
            entry = default;
            return false;
        }

        public ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
        {
            InputFile file = _files[entry.AssetGuid];
            Interlocked.Increment(ref _opens);
            return ValueTask.FromResult<IRangeSource>(new CountedRanges(FileRangeSource.Open(file.Path), this, file.Data));
        }

        public void RecordRead(long offset, int length, Payload[] data)
        {
            Interlocked.Add(ref _sourceBytes, length);
            foreach (Payload payload in data)
            {
                long bytes = Math.Min(offset + length, payload.Chunk.EndOffset) - Math.Max(offset, payload.Chunk.Offset);
                if (bytes <= 0) continue;
                switch (payload.Kind)
                {
                    case PayloadKind.Ir:
                        Interlocked.Increment(ref _irReads); Interlocked.Add(ref _irBytes, bytes); break;
                    case PayloadKind.Code:
                        Interlocked.Increment(ref _codeReads); Interlocked.Add(ref _codeBytes, bytes); break;
                    case PayloadKind.Pixels:
                        Interlocked.Increment(ref _pixelReads); Interlocked.Add(ref _pixelBytes, bytes); break;
                }
            }
        }
    }

    private sealed class CountedRanges(IRangeSource source, MeasuredStorage storage, Payload[] data) : IRangeSource
    {
        public long Length => source.Length;
        public string Generation => source.Generation;
        public bool LeasesAreImmutable => source.LeasesAreImmutable;
        public bool RetainsResidentBacking => source.RetainsResidentBacking;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            await source.ReadExactlyAsync(offset, destination, cancellationToken);
            storage.RecordRead(offset, destination.Length, data);
        }
        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
        {
            RangeLease result = await source.AcquireAsync(offset, length, cancellationToken);
            storage.RecordRead(offset, length, data);
            return result;
        }
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}
