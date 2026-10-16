using System.Diagnostics;
using System.Globalization;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using EngineTexture = SomeEngine.Resources.Texture;

/// <summary>Public-API measurements of managed asset data and repeated creation.</summary>
internal static class AssetMeasurements
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "storage") return await StorageComparison.RunAsync(args[1..]);
        if (args.Length == 1 && args[0] == "cache") return await AssetByteCacheMeasurements.RunAsync();
        int samples = 31;
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--samples"
            || !int.TryParse(args[1], out samples) || samples is < 3 or > 301))
        { Console.Error.WriteLine("Usage: --samples 3..301, or storage followed by storage comparison options."); return 2; }
        string directory = Path.Combine(Path.GetTempPath(), $"someengine-asset-measurements-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Console.WriteLine($"asset-measurements: managed-arrays samples={samples} dotnet={Environment.Version} allocation-counter=whole-process file-cache=uncontrolled");
            foreach (var size in new (uint Width, uint Height)[] { (64, 64), (2048, 1024), (4096, 4096) })
                await RunSeriesAsync(directory, size.Width, size.Height, samples);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task RunSeriesAsync(string directory, uint width, uint height, int samples)
    {
        string input = Path.Combine(directory, "input.texture.asset"), output = Path.Combine(directory, "saved.texture.asset");
        using (var asset = new TextureAsset { Name = "measured texture" })
        {
            TextureBlock block = asset.GetBlock<TextureBlock>();
            block.Dimension = TextureDimension.Texture2D;
            block.Width = width; block.Height = height; block.Depth = block.ArrayLayerCount = block.MipLevelCount = 1;
            block.Format = block.SampledFormat = Format.R8UNorm;
            block.SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D;
            byte[] pixels = new byte[checked((int)(width * height))]; Array.Fill(pixels, (byte)71);
            var tile = TextureTiles.Describe(new TextureMipTile
                { Width = width, Height = height, RowPitch = width, SlicePitch = (ulong)pixels.Length }, pixels.Length);
            block.MipTiles = [tile]; block.SetBytes(tile.ChunkKey, pixels);
            await asset.SaveAsync(input);
        }
        _ = await MeasureAsync(input, output);
        var rounds = new Measurement[samples][];
        for (int index = 0; index < samples; index++) rounds[index] = await MeasureAsync(input, output);
        string[] names = ["open", "duplicate-metadata", "save-unloaded", "read-pixels", "replace-pixels", "create", "create-shared"];
        for (int step = 0; step < names.Length; step++)
        {
            double[] times = rounds.Select(row => row[step].Milliseconds).Order().ToArray();
            long[] allocations = rounds.Select(row => row[step].AllocatedBytes).Order().ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"asset-data: operation={names[step]} pixels={width * height} p50={times[samples / 2]:F3}ms p95={times[Math.Min(samples - 1, (int)(samples * .95))]:F3}ms allocation-p50={allocations[samples / 2]}"));
        }
        File.Delete(input); File.Delete(output);
    }

    private static async Task<Measurement[]> MeasureAsync(string input, string output)
    {
        var measurements = new List<Measurement>();
        TextureAsset? asset = null, edited = null;
        EngineTexture? first = null, second = null;
        try
        {
            await Record(async () => { asset = await TextureAsset.OpenAsync(input); });
            TextureBlock block = asset!.GetBlock<TextureBlock>();
            ulong key = block.MipTiles[0].ChunkKey;
            if (block.Chunks.Count != 0) throw new InvalidOperationException("Opening metadata loaded pixel data.");
            await Record(() => { edited = asset.Duplicate(); edited.Name = "metadata edit"; return ValueTask.CompletedTask; });
            await Record(async () => { await edited!.SaveAsync(output); });
            if (block.Chunks.Count != 0) throw new InvalidOperationException("Saving metadata changed the data definition.");
            ReadOnlyMemory<byte> original = default;
            await Record(async () => { original = await block.ReadBytesAsync(key); });
            TextureBlock changed = edited!.GetBlock<TextureBlock>();
            await Record(() =>
            {
                byte[] bytes = original.ToArray(); bytes[bytes.Length / 2] = 42; changed.SetBytes(key, bytes);
                return ValueTask.CompletedTask;
            });
            await Record(async () => { first = await changed.CreateAsync(); });
            await Record(async () => { second = await changed.CreateAsync(); });
            if (!first!.TryGetTile(key, out var firstPixels) || !second!.TryGetTile(key, out var secondPixels)
                || !firstPixels.Equals(secondPixels)) throw new InvalidOperationException("Repeated creation copied immutable pixels.");
            asset.Dispose(); edited.Dispose();
            if (firstPixels.Span[firstPixels.Length / 2] != 42 || original.Span[original.Length / 2] != 71)
                throw new InvalidOperationException("Replacing pixels changed an existing value.");
            return measurements.ToArray();
        }
        finally { edited?.Dispose(); asset?.Dispose(); }

        async ValueTask Record(Func<ValueTask> operation)
        {
            long allocation = GC.GetTotalAllocatedBytes(precise: true), start = Stopwatch.GetTimestamp();
            await operation();
            measurements.Add(new(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocation));
        }
    }
    private readonly record struct Measurement(double Milliseconds, long AllocatedBytes);
}
